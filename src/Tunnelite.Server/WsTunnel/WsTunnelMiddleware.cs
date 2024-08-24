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

        if (!_tunnelStore.Connections.TryGetValue(tunnel!.ClientId, out var connectionId))
        {
            await _next(context);
            return;
        }

        var requestId = Guid.NewGuid();

        try
        {
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();

            _logger.LogInformation("WebSocket connection accepted: {requestId}", requestId);

            var completionTask = _requestsQueue.WaitForCompletionAsync(tunnel!.ClientId, requestId, webSocket);

            await _hubContext.Clients.Client(connectionId!).SendAsync("NewWsConnection", new WsConnection
            {
                RequestId = requestId,
                Path = $"{ConvertHttpToWsUri(tunnel.LocalUrl)}{path}{context.Request.QueryString}",
            });

            await completionTask;
        }
        catch (TaskCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling WebSocket connection");
        }
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

        return wsUri.Uri.ToString();
    }
}
