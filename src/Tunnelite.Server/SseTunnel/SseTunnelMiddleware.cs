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
    public async Task InvokeAsync(HttpContext context)
    {
        // Check if this is an SSE request by looking at the Accept header
        bool isSseRequest = context.Request.Headers.Accept
            .Any(h => h?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true);

        if (!isSseRequest)
        {
            await next(context);
            return;
        }

        var path = context.Request.Path.ToString();

        if (path == "/wsshttptunnel" || path == "/wsstcptunnel")
        {
            await next(context);
            return;
        }

        if (!tunnelStore.TryResolveActiveTunnel(context, out var tunnel, out var connectionId))
        {
            await next(context);
            return;
        }

        // New clients stream the full HTTP response, preserving authentication, errors and resume headers.
        if (tunnel.ProtocolVersion >= 2)
        {
            await next(context);
            return;
        }

        var requestId = Guid.NewGuid();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, connectionId.Disconnected);
        try
        {
            // Set up SSE connection headers
            context.Response.Headers.TryAdd("Content-Type", "text/event-stream");
            context.Response.Headers.TryAdd("Cache-Control", "no-cache");
            context.Response.Headers.TryAdd("Connection", "keep-alive");

            logger.LogInformation("SSE connection accepted: {requestId}", requestId);

            // Register the SSE connection with the queue
            var completionTask = requestsQueue.WaitForCompletionAsync(
                tunnel.ClientId,
                requestId,
                context, connectionId.ConnectionId);

            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            var content = await reader.ReadToEndAsync(lifetime.Token);

            // Notify the client about new SSE connection
            await hubContext.Clients.Client(connectionId.ConnectionId).SendAsync("NewSseConnection", new SseConnection
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
            logger.LogError(ex, "Error handling SSE connection");
        }
        finally { await requestsQueue.CompleteAsync(tunnel.ClientId, requestId); }
    }
}
