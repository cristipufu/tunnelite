using System.Collections.Concurrent;

namespace Tunnelite.Server.HttpTunnel;

public class HttpRequestsQueue
{
    private readonly ConcurrentDictionary<Guid, HttpDefferedRequest> PendingRequests = new();

    public virtual Task WaitForCompletionAsync(Guid requestId, HttpContext context, TimeSpan? timeout = null, CancellationToken cancellationToken = default, Guid clientId = default, string? connectionId = null)
    {
        HttpDefferedRequest request = new()
        {
            ClientId = clientId,
            ConnectionId = connectionId,
            HttpContext = context,
            RequestId = requestId,
            TimeoutCancellationTokenSource = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : new CancellationTokenSource(),
            TaskCompletionSource = new TaskCompletionSource(),
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
                // When the request gets canceled
                var request = (HttpDefferedRequest)obj!;

                if (request.TimeoutCancellationTokenSource!.IsCancellationRequested)
                {
                    request.TaskCompletionSource!.TrySetResult();
                }
                else
                {
                    // Canceled by caller
                    request.TaskCompletionSource!.TrySetCanceled(request.CancellationTokenSource!.Token);
                }

                PendingRequests.TryRemove(request.RequestId, out var _);

                request.Dispose();

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

    public virtual HttpContext? GetHttpContext(Guid requestId)
    {
        if (!PendingRequests.TryGetValue(requestId, out var request))
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

        if (!request.TaskCompletionSource!.Task.IsCompleted)
        {
            // Try to complete the task 
            if (request.TaskCompletionSource?.TrySetResult() == false)
            {
                // The request was canceled
            }
        }
        else
        {
            // The request was canceled while pending
        }

        request.Dispose();

        return Task.CompletedTask;
    }
}
