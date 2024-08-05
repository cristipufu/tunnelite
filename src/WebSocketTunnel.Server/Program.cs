using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using WebSocketTunnel.Server.Request;
using WebSocketTunnel.Server.Tunnel;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<TunnelStore>();
builder.Services.AddSingleton<RequestsQueue>();

var signalRConnectionString = builder.Configuration.GetConnectionString("AzureSignalR");

var signalRBuilder = builder.Services.AddSignalR()
                            .AddMessagePackProtocol();

if (!string.IsNullOrEmpty(signalRConnectionString))
{
    signalRBuilder.AddAzureSignalR(opt =>
    {
        opt.ConnectionString = signalRConnectionString;
    });
}

builder.Services.Configure<HubOptions>(options =>
{
    options.MaximumReceiveMessageSize = 1024 * 1024; // 1MB
    options.MaximumParallelInvocationsPerClient = 128;
    options.StreamBufferCapacity = 128;
});

var app = builder.Build();

app.UseStaticFiles();

app.UseHttpsRedirection();

app.MapPost("/tunnelite/tunnel", async (HttpContext context, [FromBody] Tunnel payload, TunnelStore tunnelStore, ILogger<Program> logger) =>
{
    try
    {
        if (string.IsNullOrEmpty(payload.LocalUrl))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Missing or invalid 'LocalUrl' property.");
            return;
        }

        if (payload.ClientId == Guid.Empty)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Missing or invalid 'ClientId' property.");
            return;
        }

        if (string.IsNullOrEmpty(payload.Subdomain))
        {
            payload.Subdomain = DnsBuilder.RandomSubdomain();
        }
        else
        {
            // don't hijack existing subdomain from another client
            if (tunnelStore.Tunnels.TryGetValue(payload.Subdomain, out var tunnel))
            {
                if (tunnel.ClientId != payload.ClientId)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("Missing or invalid 'Subdomain' property.");
                    return;
                }
            }
        }

        payload.LocalUrl = payload.LocalUrl.TrimEnd(['/']);

        tunnelStore.Tunnels.AddOrUpdate(payload.Subdomain, payload, (key, oldValue) => payload);
        tunnelStore.Clients.AddOrUpdate(payload.ClientId, payload.Subdomain, (key, oldValue) => payload.Subdomain);

        var tunnelUrl = $"{context.Request.Scheme}://{payload.Subdomain}.{context.Request.Host}{context.Request.PathBase}";

        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(new
        {
            TunnelUrl = tunnelUrl,
            payload.Subdomain,
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error creating tunnel: {Message}", ex.Message);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new
        {
            Message = "An error occurred while creating the tunnel",
            Error = ex.Message,
        });
    }
});

app.MapGet("/tunnelite/request/{requestId}", async (HttpContext context, [FromRoute] Guid requestId, RequestsQueue requestsQueue, ILogger<Program> logger) =>
{
    try
    {
        var deferredHttpContext = requestsQueue.GetHttpContext(requestId);

        if (deferredHttpContext == null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Send method
        context.Response.Headers.Append("X-T-Method", deferredHttpContext.Request.Method);

        // Send headers
        foreach (var header in deferredHttpContext.Request.Headers)
        {
            context.Response.Headers.Append($"X-TR-{header.Key}", header.Value.ToString());
        }

        // Stream the body
        await deferredHttpContext.Request.Body.CopyToAsync(context.Response.Body);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error fetching request body: {Message}", ex.Message);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new
        {
            Message = "An error occurred while fetching the request body",
            Error = ex.Message,
        });
    }
});

app.MapPost("/tunnelite/request/{requestId}", async (HttpContext context, [FromRoute] Guid requestId, RequestsQueue requestsQueue, ILogger<Program> logger) =>
{
    try
    {
        var deferredHttpContext = requestsQueue.GetHttpContext(requestId);

        if (deferredHttpContext == null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Set the status code
        if (context.Request.Headers.TryGetValue("X-T-Status", out var statusCodeHeader)
            && int.TryParse(statusCodeHeader, out var statusCode))
        {
            deferredHttpContext.Response.StatusCode = statusCode;
        }
        else
        {
            deferredHttpContext.Response.StatusCode = 200; // Default to 200 OK if not specified
        }

        // Copy headers from the tunneling client's request to the deferred response
        var notAllowed = new string[] { "Connection", "Transfer-Encoding", "Keep-Alive", "Upgrade", "Proxy-Connection" };

        foreach (var header in context.Request.Headers)
        {
            if (header.Key.StartsWith("X-TR-"))
            {
                var headerKey = header.Key[5..]; // Remove "X-TR-" prefix

                if (!notAllowed.Contains(headerKey))
                {
                    deferredHttpContext.Response.Headers.TryAdd(headerKey, header.Value);
                }
            }

            if (header.Key.StartsWith("X-TC-"))
            {
                var headerKey = header.Key[5..]; // Remove "X-TR-" prefix

                if (!notAllowed.Contains(headerKey))
                {
                    deferredHttpContext.Response.Headers.TryAdd(headerKey, header.Value);
                }
            }
        }

        // Stream the body from the tunneling client's request to the deferred response
        await context.Request.Body.CopyToAsync(deferredHttpContext.Response.Body);

        // Complete the deferred response
        await requestsQueue.CompleteAsync(requestId);

        // Send a confirmation response to the tunneling client
        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(new { Message = "Ok" });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error forwarding response body: {Message}", ex.Message);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new
        {
            Message = "An error occurred while forwarding the response body",
            Error = ex.Message,
        });
    }
});

app.MapGet("/favicon.ico", async context =>
{
    context.Response.ContentType = "image/x-icon";
    await context.Response.SendFileAsync("wwwroot/favicon.ico");
});

var supportedMethods = new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS", "HEAD" };

app.MapMethods(
    pattern: "/",
    httpMethods: supportedMethods,
    handler: (HttpContext context, IHubContext<TunnelHub> hubContext, RequestsQueue requestsQueue, TunnelStore connectionStore, ILogger<Program> logger) =>
        ProxyRequestAsync(context, hubContext, requestsQueue, connectionStore, path: string.Empty, logger));

app.MapMethods(
    pattern: "/{**path}",
    httpMethods: supportedMethods,
    handler: (HttpContext context, IHubContext<TunnelHub> hubContext, RequestsQueue requestsQueue, TunnelStore connectionStore, string path, ILogger<Program> logger) =>
        ProxyRequestAsync(context, hubContext, requestsQueue, connectionStore, path, logger));

static async Task ProxyRequestAsync(HttpContext context, IHubContext<TunnelHub> hubContext, RequestsQueue requestsQueue, TunnelStore tunnelStore, string path, ILogger<Program> logger)
{
    try
    {
        Tunnel? tunnel = null;

        var subdomain = context.Request.Host.Host.Split('.')[0];

        if (subdomain.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            tunnel = tunnelStore.Tunnels.FirstOrDefault().Value;
        }
        else if (subdomain.Equals("tunnelite"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync(ResponseText.NotFound);
            return;
        }
        else
        {
            tunnelStore.Tunnels.TryGetValue(subdomain, out tunnel);
        }

        if (tunnel == null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync(ResponseText.NotFound);
            return;
        }

        if (!tunnelStore.Connections.TryGetValue(tunnel!.ClientId, out var connectionId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync(ResponseText.NotFound);
            return;
        }
        
        var requestId = Guid.NewGuid();

        var completionTask = requestsQueue.WaitForAsync(requestId, context, timeout: null, context.RequestAborted);

        await hubContext.Clients.Client(connectionId).SendAsync("StartTunnelRequest", new RequestMetadata
        {
            RequestId = requestId,
            ContentType = context.Request.ContentType,
            ContentLength = context.Request.ContentLength,
            Headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
            Method = context.Request.Method,
            Path = $"{tunnel.LocalUrl}/{path}{context.Request.QueryString}",
        });

        await completionTask;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error processing request tunnel: {Message}", ex.Message);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsync("An error occurred while processing the tunnel.");
    }
}

const int BufferSize = 512 * 1024; // 512KB

app.MapHub<TunnelHub>("/wstunnel", (opt) =>
{
    opt.TransportMaxBufferSize = BufferSize;
    opt.ApplicationMaxBufferSize = BufferSize;
});

app.Run();