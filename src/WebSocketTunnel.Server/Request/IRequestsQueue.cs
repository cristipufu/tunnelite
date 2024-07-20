namespace WebSocketTunnel.Server.Request
{
    public interface IRequestsQueue
    {
        Task WaitForAsync(Guid requestId, HttpContext context, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

        HttpContext? GetHttpContext(Guid requestId);

        Task CompleteAsync(Guid requestId);
    }
}
