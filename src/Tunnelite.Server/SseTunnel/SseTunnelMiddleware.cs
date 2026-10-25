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

    private const int MaxLegacyBodyChars = 64 * 1024;

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

        if (!_tunnelStore.TryResolveActiveTunnel(context, out var tunnel, out var connectionId))
        {
            await _next(context);
            return;
        }

        // New clients stream the full HTTP response, preserving authentication, errors and resume headers.
        if (tunnel.ProtocolVersion >= 2)
        {
            await _next(context);
            return;
        }

        var requestId = Guid.NewGuid();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, connectionId.Disconnected);
        try
        {
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            var buffer = new char[MaxLegacyBodyChars + 1];
            var totalChars = 0;
            while (totalChars <= MaxLegacyBodyChars)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(totalChars, buffer.Length - totalChars), lifetime.Token);
                if (read == 0) break;
                totalChars += read;
            }
            if (totalChars > MaxLegacyBodyChars)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
            var content = new string(buffer, 0, totalChars);

            // Set up SSE connection headers
            context.Response.Headers.TryAdd("Content-Type", "text/event-stream");
            context.Response.Headers.TryAdd("Cache-Control", "no-cache");
            context.Response.Headers.TryAdd("Connection", "keep-alive");

            _logger.LogInformation("SSE connection accepted: {requestId}", requestId);

            // Register the SSE connection with the queue
            var completionTask = _requestsQueue.WaitForCompletionAsync(
                tunnel.ClientId,
                requestId,
                context, connectionId.ConnectionId);

            // Notify the client about new SSE connection
            await _hubContext.Clients.Client(connectionId.ConnectionId).SendAsync("NewSseConnection", new SseConnection
            {
                RequestId = requestId,
                Content = content,
                ContentType = context.Request.ContentType,
                Method = context.Request.Method,
                Path = $"{tunnel.LocalUrl}{path}{context.Request.QueryString}",
            }, lifetime.Token);

            // Wait for the connection to complete
            await completionTask.WaitAsync(lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling SSE connection");
        }
        finally { await _requestsQueue.CompleteAsync(tunnel.ClientId, requestId); }
    }
}
