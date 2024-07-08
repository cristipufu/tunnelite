using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using WebSocketTunnel.Server;
using WebSocketTunnel.Server.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();

builder.Services.AddSingleton<TunnelStore>();
builder.Services.AddSingleton<RequestsQueue>();

var app = builder.Build();

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

        if (payload.InstanceId == Guid.Empty)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Missing or invalid 'InstanceId' property.");
            return;
        }

        var subdomain = Guid.NewGuid().ToString(); // todo create subdomain

        payload.LocalUrl = payload.LocalUrl.TrimEnd(['/']);

        if (!tunnelStore.Tunnels.TryAdd(subdomain, payload))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("Failed to create the tunnel.");
            return;
        }

        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";

        var tunnelUrl = $"{baseUrl}/tunnels/{subdomain}";

        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsync($"Tunnel created: {tunnelUrl}");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error creating tunnel: {Message}", ex.Message);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsync("An error occurred while creating the tunnel.");
    }
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
        else
        {
            // todo get tunnel by subdomain
            //!tunnels.TryGetValue(subdomain, out var tunnel))
        }

        if (tunnel == null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Tunnel not found.");
            return;
        }

        if (!tunnelStore.Connections.TryGetValue(tunnel!.InstanceId, out var connectionId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Client disconnected!");
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

app.MapHub<TunnelHub>("/wstunnel");

app.Run();