using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using WebSocketTunnel.Server;
using WebSocketTunnel.Server.Dns;
using WebSocketTunnel.Server.Request;
using WebSocketTunnel.Server.Tunnel;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<TunnelStore>();
builder.Services.AddSingleton<RequestsQueue>();

builder.Services.AddSignalR()
                .AddMessagePackProtocol();

builder.Services.Configure<HubOptions>(options =>
{
    options.MaximumReceiveMessageSize = 1024 * 1024; // 1MB
    options.MaximumParallelInvocationsPerClient = 128;
    options.StreamBufferCapacity = 128;
});

var app = builder.Build();

app.UseStaticFiles();

app.UseHttpsRedirection();

app.MapPost("/register-tunnel", async (HttpContext context, [FromBody] Tunnel payload, TunnelStore tunnelStore, ILogger<Program> logger) =>
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
            await context.Response.WriteAsync(AsciiText.NotFound);
            return;
        }
        else
        {
            tunnelStore.Tunnels.TryGetValue(subdomain, out tunnel);
        }

        if (tunnel == null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync(AsciiText.NotFound);
            return;
        }

        if (!tunnelStore.Connections.TryGetValue(tunnel!.ClientId, out var connectionId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync(AsciiText.NotFound);
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