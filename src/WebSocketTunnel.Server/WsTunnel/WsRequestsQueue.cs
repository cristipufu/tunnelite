using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace WebSocketTunnel.Server.WsTunnel;

public class WsRequestsQueue
{
    public ConcurrentDictionary<Guid, WsDefferedRequest> PendingRequests = new();

    public virtual Task WaitForCompletionAsync(Guid requestId, WebSocket webSocket)
    {
        WsDefferedRequest request = new()
        {
            WebSocket = webSocket,
            RequestId = requestId,
            TaskCompletionSource = new TaskCompletionSource(),
        };

        PendingRequests.TryAdd(request.RequestId, request);

        return request.TaskCompletionSource.Task;
    }

    public virtual WebSocket? GetWebSocket(Guid requestId)
    {
        if (!PendingRequests.TryGetValue(requestId, out var request))
        {
            return null;
        }

        return request.WebSocket;
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

        return Task.CompletedTask;
    }
}
