using System.Collections.Concurrent;
using System.Net.Sockets;

#nullable disable
namespace Tunnelite.Server.TcpTunnel;

public class TcpClientStore
{
    private sealed class ClientGate
    {
        private int _leases = 1;

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public bool TryAcquireLease()
        {
            while (true)
            {
                var current = Volatile.Read(ref _leases);
                if (current <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _leases, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        public bool ReleaseLease() => Interlocked.Decrement(ref _leases) == 0;
    }

    private readonly ConcurrentDictionary<Guid, ClientGate> _clientGates = new();
    // client, [requestId, TcpClient]
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, TcpClient>> PendingRequests = new();
    // clientId, TcpListener
    private readonly ConcurrentDictionary<Guid, TcpListenerContext> Listeners = new();

    public int ActiveGateCount => _clientGates.Count;

    private ClientGate AcquireClientGate(Guid clientId)
    {
        while (true)
        {
            if (_clientGates.TryGetValue(clientId, out var existing))
            {
                if (existing.TryAcquireLease())
                {
                    return existing;
                }

                _clientGates.TryRemove(KeyValuePair.Create(clientId, existing));
            }

            var created = new ClientGate();
            if (_clientGates.TryAdd(clientId, created))
            {
                return created;
            }
        }
    }

    private async ValueTask<ClientGate> EnterClientGateAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        var gate = AcquireClientGate(clientId);
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
            return gate;
        }
        catch
        {
            ExitClientGate(clientId, gate, enteredSemaphore: false);
            throw;
        }
    }

    private void ExitClientGate(Guid clientId, ClientGate gate, bool enteredSemaphore = true)
    {
        if (enteredSemaphore)
        {
            gate.Semaphore.Release();
        }

        if (gate.ReleaseLease())
        {
            _clientGates.TryRemove(KeyValuePair.Create(clientId, gate));
        }
    }

    public void AddTcpClient(Guid clientId, Guid requestId, TcpClient tcpClient)
    {
        PendingRequests.AddOrUpdate(
            clientId,
            _ => new ConcurrentDictionary<Guid, TcpClient> { [requestId] = tcpClient },
            (_, tcpClients) =>
            {
                tcpClients[requestId] = tcpClient;
                return tcpClients;
            });
    }

    public TcpClient GetTcpClient(Guid clientId, Guid requestId)
    {
        if (!PendingRequests.TryGetValue(clientId, out var tcpClients))
        {
            return null;
        }

        tcpClients.TryGetValue(requestId, out var tcpClient);

        return tcpClient;
    }

    public void DisposeTcpClient(Guid clientId, Guid requestId)
    {
        if (!PendingRequests.TryGetValue(clientId, out var tcpClients))
        {
            return;
        }

        if (!tcpClients.TryRemove(requestId, out var tcpClient))
        {
            return;
        }

        tcpClient?.Dispose();
    }

    public async Task SetActiveConnectionAsync(TcpTunnelStore tunnelStore, Guid clientId, string connectionId, CancellationToken cancellationToken = default)
    {
        var gate = await EnterClientGateAsync(clientId, cancellationToken);
        try
        {
            tunnelStore.Connections[clientId] = connectionId;
        }
        finally
        {
            ExitClientGate(clientId, gate);
        }
    }

    public async Task<bool> TryBeginRegistrationAsync(TcpTunnelStore tunnelStore, Guid clientId, string connectionId, CancellationToken cancellationToken = default)
    {
        var gate = await EnterClientGateAsync(clientId, cancellationToken);
        try
        {
            if (tunnelStore.Connections.GetValueOrDefault(clientId) != connectionId)
            {
                return false;
            }

            if (Listeners.TryGetValue(clientId, out var previousListener)
                && tunnelStore.Connections.GetValueOrDefault(clientId) == connectionId
                && Listeners.TryRemove(KeyValuePair.Create(clientId, previousListener)))
            {
                RemovePendingRequestsCore(clientId);
                if (previousListener != null)
                {
                    await previousListener.DisposeAsync();
                }
            }

            return tunnelStore.Connections.GetValueOrDefault(clientId) == connectionId;
        }
        finally
        {
            ExitClientGate(clientId, gate);
        }
    }

    public async Task<bool> TryCompleteRegistrationAsync(TcpTunnelStore tunnelStore, Guid clientId, string connectionId, TcpListenerContext tcpListener, CancellationToken cancellationToken = default)
    {
        var gate = await EnterClientGateAsync(clientId, cancellationToken);
        try
        {
            if (tunnelStore.Connections.GetValueOrDefault(clientId) != connectionId)
            {
                return false;
            }

            TcpListenerContext displaced = null;
            var registered = Listeners.AddOrUpdate(
                clientId,
                _ =>
                {
                    displaced = null;
                    return tcpListener;
                },
                (_, previous) =>
                {
                    if (tunnelStore.Connections.GetValueOrDefault(clientId) != connectionId)
                    {
                        displaced = null;
                        return previous;
                    }

                    displaced = ReferenceEquals(previous, tcpListener) ? null : previous;
                    return tcpListener;
                });

            if (!ReferenceEquals(registered, tcpListener))
            {
                return false;
            }

            if (tunnelStore.Connections.GetValueOrDefault(clientId) != connectionId)
            {
                Listeners.TryRemove(KeyValuePair.Create(clientId, tcpListener));
                if (displaced != null)
                {
                    await displaced.DisposeAsync();
                }

                return false;
            }

            if (displaced != null)
            {
                await displaced.DisposeAsync();
            }

            return true;
        }
        finally
        {
            ExitClientGate(clientId, gate);
        }
    }

    public async Task DisconnectAsync(TcpTunnelStore tunnelStore, Guid clientId, string connectionId)
    {
        var gate = await EnterClientGateAsync(clientId);
        try
        {
            if (!tunnelStore.Connections.TryRemove(new KeyValuePair<Guid, string>(clientId, connectionId)))
            {
                return;
            }

            if (Listeners.TryGetValue(clientId, out var existingListener)
                && (existingListener.ConnectionId is null || existingListener.ConnectionId == connectionId)
                && Listeners.TryRemove(KeyValuePair.Create(clientId, existingListener)))
            {
                await existingListener.DisposeAsync();
            }

            RemovePendingRequestsCore(clientId);
        }
        finally
        {
            ExitClientGate(clientId, gate);
        }
    }

    public Task DisposeTcpListenerAsync(TcpTunnelStore tunnelStore, Guid clientId, TcpListenerContext expectedListener = null) =>
        DisposeTcpListenerAsync(clientId, expectedListener);

    public async Task DisposeTcpListenerAsync(Guid clientId, TcpListenerContext expectedListener = null)
    {
        var gate = await EnterClientGateAsync(clientId);
        try
        {
            if (expectedListener is not null)
            {
                if (Listeners.TryRemove(KeyValuePair.Create(clientId, expectedListener)))
                {
                    RemovePendingRequestsCore(clientId);
                }

                await expectedListener.DisposeAsync();
                return;
            }

            if (Listeners.TryRemove(clientId, out var listener) && listener != null)
            {
                await listener.DisposeAsync();
            }

            RemovePendingRequestsCore(clientId);
        }
        finally
        {
            ExitClientGate(clientId, gate);
        }
    }

    private void RemovePendingRequestsCore(Guid clientId)
    {
        if (PendingRequests.TryRemove(clientId, out var tcpClients))
        {
            foreach (var (_, client) in tcpClients)
            {
                client?.Dispose();
            }
        }
    }
}

public class TcpListenerContext : IAsyncDisposable
{
    private int _disposed;

    public string ConnectionId { get; set; }

    public TcpListener TcpListener { get; set; }

    public CancellationTokenSource CancellationTokenSource { get; set; }

    public Task AcceptConnectionsTask { get; set; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (CancellationTokenSource != null)
        {
            await CancellationTokenSource.CancelAsync();
            CancellationTokenSource.Dispose();
        }

        TcpListener?.Dispose();

        GC.SuppressFinalize(this);
    }
}