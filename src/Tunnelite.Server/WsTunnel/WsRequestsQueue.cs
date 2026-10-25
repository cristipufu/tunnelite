using System.Net.WebSockets;

namespace Tunnelite.Server.WsTunnel;

public class WsRequestsQueue() : StreamRequestsQueue<WebSocket>(static socket => socket.Abort())
{
    public virtual WebSocket? GetWebSocket(Guid clientId, Guid requestId) => Get(clientId, requestId);
}
