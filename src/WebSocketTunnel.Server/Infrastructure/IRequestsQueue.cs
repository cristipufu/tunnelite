namespace WebSocketTunnel.Server.Infrastructure
{
    public interface IRequestsQueue
    {
        Task WaitForAsync(Guid requestId, HttpContext context, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

        HttpContext? GetHttpContext(Guid requestId);

        Task CompleteAsync(Guid requestId);
    }
}
