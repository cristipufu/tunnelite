namespace Tunnelite.Server.SseTunnel;

public class SseRequestsQueue() : StreamRequestsQueue<HttpContext>(static context => context.Abort())
{
    public virtual HttpContext? GetHttpContext(Guid clientId, Guid requestId) => Get(clientId, requestId);
}
