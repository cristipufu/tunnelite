using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Tunnelite.Server.WsTunnel;

public class WsRequestsQueue
{
    // Flat keys release all per-client state when the last request completes.
    private readonly ConcurrentDictionary<(Guid ClientId, Guid RequestId), WsDefferedRequest> PendingRequests = new();

    public virtual Task WaitForCompletionAsync(Guid clientId, Guid requestId, WebSocket webSocket, string? connectionId = null)
    {
        var request = new WsDefferedRequest
        {
            ConnectionId = connectionId, WebSocket = webSocket, RequestId = requestId,
            TaskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        if (!PendingRequests.TryAdd((clientId, requestId), request))
            throw new InvalidOperationException("The stream request is already pending.");
        return request.TaskCompletionSource.Task;
    }

    public virtual WebSocket? GetWebSocket(Guid clientId, Guid requestId) =>
        PendingRequests.TryGetValue((clientId, requestId), out var request) ? request.WebSocket : null;

    public virtual Task CompleteAsync(Guid clientId, Guid requestId)
    {
        if (PendingRequests.TryRemove((clientId, requestId), out var request))
            request.TaskCompletionSource?.TrySetResult();
        return Task.CompletedTask;
    }

    public virtual Task CompleteAsync(Guid clientId) => CompleteMatchingAsync(clientId, null);

    public Task CompleteConnectionAsync(Guid clientId, string connectionId) => CompleteMatchingAsync(clientId, connectionId);

    private Task CompleteMatchingAsync(Guid clientId, string? connectionId)
    {
        foreach (var entry in PendingRequests)
        {
            if (entry.Key.ClientId != clientId || (connectionId is not null && entry.Value.ConnectionId != connectionId)
                || !PendingRequests.TryRemove(entry)) continue;
            try { entry.Value.WebSocket?.Abort(); }
            finally { entry.Value.TaskCompletionSource?.TrySetResult(); }
        }
        return Task.CompletedTask;
    }
}
