using System.Collections.Concurrent;
namespace Tunnelite.Server.SseTunnel;

public class SseRequestsQueue
{
    // client, [requestId, SseDeferredRequest]
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, SseDeferredRequest>> PendingRequests = new();

    public Task WaitForCompletionAsync(Guid clientId, Guid requestId, HttpContext context)
    {
        SseDeferredRequest request = new()
        {
            HttpContext = context,
            RequestId = requestId,
            TaskCompletionSource = new TaskCompletionSource(),
        };

        PendingRequests.AddOrUpdate(
            clientId,
            _ => new ConcurrentDictionary<Guid, SseDeferredRequest> { [requestId] = request },
            (_, requests) =>
            {
                requests[requestId] = request;
                return requests;
            });

        return request.TaskCompletionSource.Task;
    }

    public virtual HttpContext? GetHttpContext(Guid clientId, Guid requestId)
    {
        if (!PendingRequests.TryGetValue(clientId, out var requests))
        {
            return null;
        }

        requests.TryGetValue(requestId, out var request);

        return request?.HttpContext;
    }

    public virtual Task CompleteAsync(Guid clientId, Guid requestId)
    {
        if (!PendingRequests.TryGetValue(clientId, out var requests))
        {
            return Task.CompletedTask;
        }

        if (!requests.TryRemove(requestId, out var request))
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

        return Task.CompletedTask;
    }

    public virtual async Task CompleteAsync(Guid clientId)
    {
        if (!PendingRequests.TryRemove(clientId, out var requests))
        {
            return;
        }

        foreach (var request in requests)
        {
            await CompleteAsync(clientId, request.Key);
        }
    }
}