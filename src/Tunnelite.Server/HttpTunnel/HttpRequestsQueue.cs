using System.Collections.Concurrent;

namespace Tunnelite.Server.HttpTunnel;

public class HttpRequestsQueue
{
    private readonly ConcurrentDictionary<Guid, HttpDeferredRequest> PendingRequests = new();

    public virtual Task WaitForCompletionAsync(Guid requestId, HttpContext context, TimeSpan? timeout = null, CancellationToken cancellationToken = default, Guid clientId = default, string? connectionId = null)
    {
        HttpDeferredRequest request = new()
        {
            ClientId = clientId,
            ConnectionId = connectionId,
            HttpContext = context,
            RequestId = requestId,
            TimeoutCancellationTokenSource = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : new CancellationTokenSource(),
            TaskCompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        // Wait until caller cancels or timeout expires
        request.CancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            request.TimeoutCancellationTokenSource.Token);

        PendingRequests.TryAdd(request.RequestId, request);

        if (request.CancellationTokenSource.Token.CanBeCanceled)
        {
            request.CancellationTokenRegistration = request.CancellationTokenSource.Token.Register(obj =>
            {
                var canceled = (HttpDeferredRequest)obj!;
                if (!PendingRequests.TryRemove(canceled.RequestId, out _))
                {
                    return;
                }

                if (canceled.TimeoutCancellationTokenSource!.IsCancellationRequested)
                {
                    canceled.TaskCompletionSource!.TrySetResult();
                }
                else
                {
                    canceled.TaskCompletionSource!.TrySetCanceled(canceled.CancellationTokenSource!.Token);
                }

                canceled.Dispose();
            }, request);
        }

        return request.TaskCompletionSource.Task;
    }

    public Guid GetClientId(Guid requestId) => PendingRequests.TryGetValue(requestId, out var request) ? request.ClientId : Guid.Empty;

    public async Task CompleteConnectionAsync(string connectionId)
    {
        foreach (var request in PendingRequests.Values.Where(r => r.ConnectionId == connectionId))
        {
            request.HttpContext?.Abort();
            await CompleteAsync(request.RequestId);
        }
    }

    public virtual HttpContext? GetHttpContext(Guid requestId) =>
        PendingRequests.TryGetValue(requestId, out var request) ? request.HttpContext : null;

    /// <summary>
    /// Atomically claims the pending HTTP response so concurrent POST/DELETE calls for the same
    /// <paramref name="requestId"/> cannot mutate the <see cref="HttpContext"/> simultaneously.
    /// </summary>
    public virtual HttpContext? ClaimResponseHttpContext(Guid requestId)
    {
        if (!PendingRequests.TryGetValue(requestId, out var request) || !request.TryClaimResponse())
        {
            return null;
        }

        return request.HttpContext;
    }

    public virtual Task CompleteAsync(Guid requestId)
    {
        if (!PendingRequests.TryRemove(requestId, out var request))
        {
            return Task.CompletedTask;
        }

        request.TaskCompletionSource?.TrySetResult();
        request.Dispose();

        return Task.CompletedTask;
    }
}
