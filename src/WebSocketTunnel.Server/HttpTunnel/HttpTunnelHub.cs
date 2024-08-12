using Microsoft.AspNetCore.SignalR;
using System.Buffers;
using System.Net.WebSockets;
using WebSocketTunnel.Server.WsTunnel;

namespace WebSocketTunnel.Server.HttpTunnel;

public class HttpTunnelHub(HttpTunnelStore httpTunnelStore, WsRequestsQueue wsRequestsQueue, ILogger<HttpTunnelHub> logger) : Hub
{
    private readonly HttpTunnelStore _httpTunnelStore = httpTunnelStore;
    private readonly WsRequestsQueue _wsRequestsQueue = wsRequestsQueue;
    private readonly ILogger _logger = logger;

    public override Task OnConnectedAsync()
    {
        var clientId = GetClientId(Context);

        _httpTunnelStore.Connections.AddOrUpdate(clientId, Context.ConnectionId, (key, oldValue) => Context.ConnectionId);

        return base.OnConnectedAsync();
    }

    public async IAsyncEnumerable<(ReadOnlyMemory<byte>, WebSocketMessageType)> StreamIncomingWsAsync(WsConnection wsConnection)
    {
        var webSocket = _wsRequestsQueue.GetWebSocket(wsConnection.RequestId);

        if (webSocket == null)
        {
            yield break;
        }

        const int chunkSize = 32 * 1024;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
        WebSocketReceiveResult result;

        try
        {
            do
            {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), Context.ConnectionAborted);

                yield return (new ReadOnlyMemory<byte>(buffer, 0, result.Count), result.MessageType);
            }
            while (!result.CloseStatus.HasValue && !Context.ConnectionAborted.IsCancellationRequested);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, result.CloseStatusDescription, CancellationToken.None); 
            }
        }
        finally
        {
            // Complete the deferred websocket request
            await _wsRequestsQueue.CompleteAsync(wsConnection.RequestId);

            _logger.LogInformation("Done reading.. Closing WebSocketConnection {RequestId}", wsConnection.RequestId);

            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task StreamOutgoingWsAsync(WsConnection wsConnection, IAsyncEnumerable<(ReadOnlyMemory<byte> Data, WebSocketMessageType Type)> stream)
    {
        var webSocket = _wsRequestsQueue.GetWebSocket(wsConnection.RequestId);

        if (webSocket == null)
        {
            return;
        }

        try
        {
            await foreach (var chunk in stream.WithCancellation(Context.ConnectionAborted))
            {
                if (chunk.Type == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                }
                else
                {
                    await webSocket.SendAsync(chunk.Data, chunk.Type, true, Context.ConnectionAborted);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred while streaming outgoing data for {RequestId}", wsConnection.RequestId);
        }
        finally
        {
            _logger.LogInformation("Done writing.. WebSocket Connection {RequestId}", wsConnection.RequestId);
        }
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var clientId = GetClientId(Context);

        if (_httpTunnelStore.Clients.TryGetValue(clientId, out var subdomain))
        {
            _httpTunnelStore.Tunnels.Remove(subdomain, out var _);
            _httpTunnelStore.Connections.Remove(clientId, out var _);
            _httpTunnelStore.Clients.Remove(clientId, out _);
        }

        // todo close and dispose all websockets for clientId

        return base.OnDisconnectedAsync(exception);
    }

    private static Guid GetClientId(HubCallerContext context)
    {
        return Guid.Parse(context.GetHttpContext()!.Request.Query["clientId"].ToString());
    }
}
