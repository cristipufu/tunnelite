using System.Collections.Concurrent;

namespace Tunnelite.Server;

/// <summary>
/// Per-client pending-stream registry for WebSocket and SSE tunnel connections.
/// Partitioning by <c>ClientId</c> isolates concurrent tenants on a multi-client server, while empty
/// per-client dictionaries are pruned automatically so disconnected clients leave no state behind.
/// </summary>
public abstract class StreamRequestsQueue<TResource>(Action<TResource> abort) where TResource : class
{
    private sealed class PendingStream
    {
        public required string? ConnectionId { get; init; }
        public required TResource Resource { get; init; }
        public required TaskCompletionSource Completion { get; init; }
    }

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, PendingStream>> _pendingByClient = new();

    public virtual Task WaitForCompletionAsync(Guid clientId, Guid requestId, TResource resource, string? connectionId = null)
    {
        var entry = new PendingStream
        {
            ConnectionId = connectionId,
            Resource = resource,
            Completion = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var clientStreams = _pendingByClient.GetOrAdd(clientId, static _ => new());
        if (!clientStreams.TryAdd(requestId, entry))
        {
            throw new InvalidOperationException("The stream request is already pending.");
        }

        return entry.Completion.Task;
    }

    protected TResource? Get(Guid clientId, Guid requestId) =>
        _pendingByClient.TryGetValue(clientId, out var clientStreams)
        && clientStreams.TryGetValue(requestId, out var entry)
            ? entry.Resource
            : null;

    public virtual Task CompleteAsync(Guid clientId, Guid requestId)
    {
        if (_pendingByClient.TryGetValue(clientId, out var clientStreams)
            && clientStreams.TryRemove(requestId, out var entry))
        {
            entry.Completion.TrySetResult();
            PruneIfEmpty(clientId, clientStreams);
        }

        return Task.CompletedTask;
    }

    public virtual Task CompleteAsync(Guid clientId) => CompleteMatchingAsync(clientId, connectionId: null);

    public Task CompleteConnectionAsync(Guid clientId, string connectionId) => CompleteMatchingAsync(clientId, connectionId);

    private Task CompleteMatchingAsync(Guid clientId, string? connectionId)
    {
        if (!_pendingByClient.TryGetValue(clientId, out var clientStreams))
        {
            return Task.CompletedTask;
        }

        foreach (var (requestId, entry) in clientStreams)
        {
            if ((connectionId is not null && entry.ConnectionId != connectionId)
                || !clientStreams.TryRemove(requestId, out var removed))
            {
                continue;
            }

            try
            {
                abort(removed.Resource);
            }
            finally
            {
                removed.Completion.TrySetResult();
            }
        }

        PruneIfEmpty(clientId, clientStreams);
        return Task.CompletedTask;
    }

    private void PruneIfEmpty(Guid clientId, ConcurrentDictionary<Guid, PendingStream> clientStreams)
    {
        if (clientStreams.IsEmpty)
        {
            _pendingByClient.TryRemove(KeyValuePair.Create(clientId, clientStreams));
        }
    }
}
