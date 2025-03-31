using Microsoft.AspNetCore.SignalR;
using Tunnelite.Server.HttpTunnel;

namespace Tunnelite.Server.SseTunnel;

public class SseTunnelMiddleware(
    RequestDelegate next,
    SseRequestsQueue requestsQueue,
    HttpTunnelStore tunnelStore,
    IHubContext<HttpTunnelHub> hubContext,
    ILogger<SseTunnelMiddleware> logger)
{
    private readonly RequestDelegate _next = next;
    private readonly HttpTunnelStore _tunnelStore = tunnelStore;
    private readonly SseRequestsQueue _requestsQueue = requestsQueue;
    private readonly IHubContext<HttpTunnelHub> _hubContext = hubContext;
    private readonly ILogger<SseTunnelMiddleware> _logger = logger;

    public async Task InvokeAsync(HttpContext context)
    {
        // Check if this is an SSE request by looking at the Accept header
        bool isSseRequest = context.Request.Headers.Accept
            .Any(h => h?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true);

        if (!isSseRequest)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.ToString();

        if (path == "/wsshttptunnel" || path == "/wsstcptunnel")
        {
            await _next(context);
            return;
        }

        HttpTunnelRequest? tunnel = null;
        var subdomain = context.Request.Host.Host.Split('.')[0];

        if (subdomain.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            tunnel = _tunnelStore.Tunnels.FirstOrDefault().Value;
        }
        else if (subdomain.Equals("tunnelite"))
        {
            await _next(context);
            return;
        }
        else
        {
            _tunnelStore.Tunnels.TryGetValue(subdomain, out tunnel);
        }

        if (tunnel == null)
        {
            await _next(context);
            return;
        }

        if (!_tunnelStore.Connections.TryGetValue(tunnel.ClientId, out var connectionId))
        {
            await _next(context);
            return;
        }

        var requestId = Guid.NewGuid();
        try
        {
            // Set up SSE connection headers
            context.Response.Headers.TryAdd("Content-Type", "text/event-stream");
            context.Response.Headers.TryAdd("Cache-Control", "no-cache");
            context.Response.Headers.TryAdd("Connection", "keep-alive");

            _logger.LogInformation("SSE connection accepted: {requestId}", requestId);

            // Register the SSE connection with the queue
            var completionTask = _requestsQueue.WaitForCompletionAsync(
                tunnel.ClientId,
                requestId,
                context);

            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            var content = await reader.ReadToEndAsync();

            // Notify the client about new SSE connection
            await _hubContext.Clients.Client(connectionId).SendAsync("NewSseConnection", new SseConnection
            {
                RequestId = requestId,
                Content = content,
                ContentType = context.Request.ContentType,
                Method = context.Request.Method,
                Path = $"{tunnel.LocalUrl}{path}{context.Request.QueryString}",
            });

            // Wait for the connection to complete
            await completionTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling SSE connection");
        }
    }
}