namespace Tunnelite.Server.HttpTunnel;

public class HttpDeferredRequest : IDisposable
{
    private int _disposed;
    private int _responseClaimed;

    public Guid ClientId { get; set; }
    public string? ConnectionId { get; set; }
    public HttpContext? HttpContext { get; set; }

    public Guid RequestId { get; set; }

    public TaskCompletionSource? TaskCompletionSource { get; set; }

    public CancellationTokenSource? TimeoutCancellationTokenSource { get; set; }

    public CancellationTokenSource? CancellationTokenSource { get; set; }

    public CancellationTokenRegistration CancellationTokenRegistration { get; set; }

    public bool TryClaimResponse() => Interlocked.Exchange(ref _responseClaimed, 1) == 0;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (disposing)
        {
            HttpContext = null;
            CancellationTokenRegistration.Dispose();
            CancellationTokenSource?.Dispose();
            TimeoutCancellationTokenSource?.Dispose();
        }
    }
}

public class HttpDefferedRequest : HttpDeferredRequest;
