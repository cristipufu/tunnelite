using System.Collections.Concurrent;
using System.Net.Sockets;

#nullable disable
namespace Tunnelite.Server.TcpTunnel;

public class TcpClientStore
{
    // client, [requestId, TcpClient]
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, TcpClient>> _clientStore = new();
    // clientId, TcpListener
    public ConcurrentDictionary<Guid, TcpListenerContext> _listenerStore = new();

    public void AddTcpClient(Guid clientId, Guid requestId, TcpClient tcpClient)
    {
        _clientStore.AddOrUpdate(
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
        if (!_clientStore.TryGetValue(clientId, out var tcpClients))
        {
            return null;
        }

        tcpClients.TryGetValue(requestId, out var tcpClient);

        return tcpClient;
    }

    public void DisposeTcpClient(Guid clientId, Guid requestId)
    {
        if (!_clientStore.TryGetValue(clientId, out var tcpClients))
        {
            return;
        }

        if (!tcpClients.TryRemove(requestId, out var tcpClient))
        {
            return;
        }

        tcpClient?.Dispose();
    }

    public void AddTcpListener(Guid clientId, TcpListenerContext tcpListener)
    {
        _listenerStore.AddOrUpdate(clientId, tcpListener, (key, oldValue) => tcpListener);
    }

    public void DisposeTcpListener(Guid clientId)
    {
        if (_clientStore.TryRemove(clientId, out var tcpClients))
        {
            foreach (var client in tcpClients.Values)
            {
                client?.Dispose();
            }
        }

        if (_listenerStore.TryRemove(clientId, out var listener))
        {
            listener?.Dispose();
        }
    }
}

public class TcpListenerContext : IDisposable
{
    public TcpListener TcpListener { get; set; }

    public CancellationTokenSource CancellationTokenSource { get; set; }

    public Task AcceptConnectionsTask { get; set; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancellationTokenSource?.Cancel();
            CancellationTokenSource?.Dispose();
            TcpListener?.Dispose();
        }
    }
}