using System.Collections.Concurrent;

namespace Tunnelite.Server;

/// <summary>
/// Shared flat-keyed pending-stream registry for WebSocket and SSE tunnel connections.
/// Flat <c>(ClientId, RequestId)</c> keys release all per-client state when the last stream completes.
/// </summary>
public abstract class StreamRequestsQueue<TResource>(Action<TResource> abort) where TResource : class
{
    private sealed class PendingStream
    {
        public required string? ConnectionId { get; init; }
        public required TResource Resource { get; init; }
        public required TaskCompletionSource Completion { get; init; }
    }

    private readonly ConcurrentDictionary<(Guid ClientId, Guid RequestId), PendingStream> _pending = new();

    public virtual Task WaitForCompletionAsync(Guid clientId, Guid requestId, TResource resource, string? connectionId = null)
    {
        var entry = new PendingStream
        {
            ConnectionId = connectionId,
            Resource = resource,
            Completion = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        if (!_pending.TryAdd((clientId, requestId), entry))
        {
            throw new InvalidOperationException("The stream request is already pending.");
        }

        return entry.Completion.Task;
    }

    protected TResource? Get(Guid clientId, Guid requestId) =>
        _pending.TryGetValue((clientId, requestId), out var entry) ? entry.Resource : null;

    public virtual Task CompleteAsync(Guid clientId, Guid requestId)
    {
        if (_pending.TryRemove((clientId, requestId), out var entry))
        {
            entry.Completion.TrySetResult();
        }

        return Task.CompletedTask;
    }

    public virtual Task CompleteAsync(Guid clientId) => CompleteMatchingAsync(clientId, connectionId: null);

    public Task CompleteConnectionAsync(Guid clientId, string connectionId) => CompleteMatchingAsync(clientId, connectionId);

    private Task CompleteMatchingAsync(Guid clientId, string? connectionId)
    {
        foreach (var entry in _pending)
        {
            if (entry.Key.ClientId != clientId
                || (connectionId is not null && entry.Value.ConnectionId != connectionId)
                || !_pending.TryRemove(entry))
            {
                continue;
            }

            try
            {
                abort(entry.Value.Resource);
            }
            finally
            {
                entry.Value.Completion.TrySetResult();
            }
        }

        return Task.CompletedTask;
    }
}
