using Microsoft.AspNetCore.SignalR;
using Tunnelite.Server.HttpTunnel;

namespace Tunnelite.Server.WsTunnel;

public class WsTunnelMiddleware(RequestDelegate next, WsRequestsQueue requestsQueue, HttpTunnelStore tunnelStore, IHubContext<HttpTunnelHub> hubContext, ILogger<WsTunnelMiddleware> logger)
{
    private readonly RequestDelegate _next = next;
    private readonly HttpTunnelStore _tunnelStore = tunnelStore;
    private readonly WsRequestsQueue _requestsQueue = requestsQueue;
    private readonly IHubContext<HttpTunnelHub> _hubContext = hubContext;
    private readonly ILogger<WsTunnelMiddleware> _logger = logger;

    private const int MaxSubProtocols = 8;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
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

        var requestId = Guid.NewGuid();

        // Browsers drop the connection when a subprotocol they asked for isn't confirmed (Vite's HMR client asks
        // for "vite-hmr"), so confirm the first one here and pass the whole list on to the tunnel client, which
        // requests the same from the local app. The list is attacker-controlled input from the public side; cap it.
        var subProtocols = context.WebSockets.WebSocketRequestedProtocols.Take(MaxSubProtocols).ToArray();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, connectionId.Disconnected);

        try
        {
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync(subProtocols.FirstOrDefault());

            _logger.LogInformation("WebSocket connection accepted: {requestId}", requestId);

            var completionTask = _requestsQueue.WaitForCompletionAsync(tunnel!.ClientId, requestId, webSocket, connectionId.ConnectionId);

            await _hubContext.Clients.Client(connectionId.ConnectionId).SendAsync("NewWsConnection", new WsConnection
            {
                RequestId = requestId,
                Path = $"{ConvertHttpToWsUri(tunnel.LocalUrl)}{path}{context.Request.QueryString}",
                SubProtocols = subProtocols.Length > 0 ? subProtocols : null,
                Headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.Select(v => v ?? "").ToArray()),
            }, lifetime.Token);

            await completionTask.WaitAsync(lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // ignore
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling WebSocket connection");
        }
        finally { await _requestsQueue.CompleteAsync(tunnel.ClientId, requestId); }
    }

    public static string ConvertHttpToWsUri(string? httpUrl)
    {
        if (httpUrl == null)
        {
            return string.Empty;
        }

        Uri originalUri = new(httpUrl);

        string scheme = originalUri.Scheme == "https" ? "wss" : "ws";
        string host = originalUri.Host;
        int port = originalUri.Port;
        string path = originalUri.AbsolutePath;
        string query = originalUri.Query;

        UriBuilder wsUri = new(scheme, host, port, path)
        {
            Query = query
        };

        return wsUri.Uri.ToString().TrimEnd(['/']);
    }
}
