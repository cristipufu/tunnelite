using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Tunnelite.Server.WsTunnel;

public class WsRequestsQueue
{
    // client, [requestId, WsDefferedRequest]
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, WsDefferedRequest>> PendingRequests = new();

    public virtual Task WaitForCompletionAsync(Guid clientId, Guid requestId, WebSocket webSocket)
    {
        WsDefferedRequest request = new()
        {
            WebSocket = webSocket,
            RequestId = requestId,
            TaskCompletionSource = new TaskCompletionSource(),
        };

        PendingRequests.AddOrUpdate(
            clientId,
            _ => new ConcurrentDictionary<Guid, WsDefferedRequest> { [requestId] = request },
            (_, requests) =>
            {
                requests[requestId] = request;
                return requests;
            });

        return request.TaskCompletionSource.Task;
    }

    public virtual WebSocket? GetWebSocket(Guid clientId, Guid requestId)
    {
        if (!PendingRequests.TryGetValue(clientId, out var requests))
        {
            return null;
        }

        requests.TryGetValue(requestId, out var request);

        return request?.WebSocket;
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
