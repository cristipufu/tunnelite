using Microsoft.AspNetCore.SignalR.Client;
using Nerdbank.MessagePack;
using Nerdbank.MessagePack.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Tunnelite.Sdk;

public class TcpTunnelClient : ITunnelClient, IAsyncDisposable
{
    public event Func<Task>? Connected;
    public event Action<string, string>? LogRequest;
    public event Action<string, string>? LogFailedRequest;
    public event Action<Exception>? LogException;
    public event Action<string>? Log;
    public event Action<string>? LogError;

    public HubConnection Connection { get; }

    public string? TunnelUrl => _currentTunnel?.TunnelUrl;

    private readonly TcpTunnelRequest Tunnel;
    private TcpTunnelResponse? _currentTunnel;
    private int _disposed;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private long _operationId;

    public TcpTunnelClient(TcpTunnelRequest tunnel, LogLevel? logLevel)
    {
        ArgumentNullException.ThrowIfNull(tunnel);
        if (tunnel.ClientId == Guid.Empty) tunnel.ClientId = Guid.NewGuid();
        Tunnel = tunnel;

        Connection = new HubConnectionBuilder()
            .WithUrl($"{Tunnel.PublicUrl.TrimEnd('/')}/wsstcptunnel?clientId={tunnel.ClientId}")
            .AddMessagePackProtocol(
                TunneliteWitness.GeneratedTypeShapeProvider,
                new MessagePackSerializer
                {
                    Converters = [new GuidAsStringConverter(), new WebSocketMessageTypeConverter()],
                })
            .ConfigureLogging(logging =>
            {
                if (logLevel.HasValue)
                {
                    logging.SetMinimumLevel(logLevel.Value);
                    logging.AddConsole();
                }
            })
            .WithAutomaticReconnect()
            .Build();

        Connection.On<TcpConnection>("NewTcpConnection", tcpConnection =>
        {
            Emit(LogRequest, "TCP", $"New Connection {tcpConnection.RequestId}");
            Track(() => HandleNewTcpConnectionAsync(tcpConnection));
            return Task.CompletedTask;
        });

        Connection.On<string>("TcpTunnelClosed", errorMessage =>
        {
            Emit(LogError, $"[TCP] Tunnel closed by server: {errorMessage}.");
            Track(async () =>
            {
                _currentTunnel = await RegisterTunnelAsync(tunnel, _lifetime.Token);
                if (!string.IsNullOrEmpty(_currentTunnel?.TunnelUrl)) await NotifyReadyAsync();
            });
            return Task.CompletedTask;
        });

        Connection.Reconnecting += _ => { _currentTunnel = null; return Task.CompletedTask; };
        Connection.Reconnected += _ => ObserveAsync(async () =>
        {
            _currentTunnel = await RegisterTunnelAsync(tunnel, _lifetime.Token);
            if (!string.IsNullOrEmpty(_currentTunnel?.TunnelUrl)) await NotifyReadyAsync();
        });

        Connection.Closed += _ => ObserveAsync(async () =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _currentTunnel = null;
            await Task.Delay(Random.Shared.Next(0, 5000), _lifetime.Token);
            await ConnectAsync(_lifetime.Token);
        });
    }

    /// <summary>
    /// Closes the tunnel and stops reconnecting.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync();
        await Connection.DisposeAsync();
        await Task.WhenAll(_connections.Values);
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    public Task ConnectAsync() => ConnectAsync(CancellationToken.None);

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _connectionGate.WaitAsync(attempt.Token);
        var ready = false;
        try
        {
            if (Connection.State == HubConnectionState.Connected && _currentTunnel is not null) return;
            if (await ConnectWithRetryAsync(Connection, attempt.Token))
            {
                _currentTunnel = await RegisterTunnelAsync(Tunnel, attempt.Token);
                ready = !string.IsNullOrEmpty(_currentTunnel?.TunnelUrl);
            }
        }
        finally { _connectionGate.Release(); }
        if (ready) await NotifyReadyAsync();
        attempt.Token.ThrowIfCancellationRequested();
    }

    public Task<TcpTunnelResponse?> RegisterTunnelAsync(TcpTunnelRequest tunnel) =>
        RegisterTunnelAsync(tunnel, _lifetime.Token);

    public async Task<TcpTunnelResponse?> RegisterTunnelAsync(TcpTunnelRequest tunnel, CancellationToken cancellationToken)
    {
        tunnel.PublicPort = _currentTunnel?.Port;

        TcpTunnelResponse? tunnelResponse = null;

        while (tunnelResponse == null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0) return null;

            try
            {
                tunnelResponse = await Connection.InvokeAsync<TcpTunnelResponse>("RegisterTunnelAsync", tunnel, cancellationToken);

                if (!string.IsNullOrEmpty(tunnelResponse.Error))
                {
                    Emit(LogError, $"[TCP] {tunnelResponse.Message}:{tunnelResponse.Error}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _disposed) != 0) return null;
                Emit(LogError, "[TCP] An error occurred while registering the tunnel:");
                Emit(LogException, ex);

                await Task.Delay(5000, cancellationToken);
            }
        }

        return tunnelResponse;
    }

    private async Task NotifyReadyAsync()
    {
        if (Connected is { } handlers)
            foreach (Func<Task> handler in handlers.GetInvocationList()) await ObserveAsync(handler);
    }

    private void Track(Func<Task> operation)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var id = Interlocked.Increment(ref _operationId);
        var task = ObserveAsync(operation);
        _connections[id] = task;
        _ = task.ContinueWith(_ => _connections.TryRemove(id, out var _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task ObserveAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (Volatile.Read(ref _disposed) == 0) Emit(LogException, exception);
        }
    }

    private static void Emit<T>(Action<T>? diagnostic, T value)
    {
        try { diagnostic?.Invoke(value); } catch { /* Diagnostics cannot interrupt transport cleanup. */ }
    }

    private static void Emit(Action<string, string>? diagnostic, string method, string path)
    {
        try { diagnostic?.Invoke(method, path); } catch { /* Diagnostics cannot interrupt transport cleanup. */ }
    }

    private async Task HandleNewTcpConnectionAsync(TcpConnection tcpConnection)
    {
        using var localClient = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);

        try
        {
            await localClient.ConnectAsync(Tunnel.Host, Tunnel.LocalPort, cts.Token);

            var incomingTask = StreamIncomingAsync(localClient, tcpConnection, cts.Token);
            var outgoingTask = StreamOutgoingAsync(localClient, tcpConnection, cts.Token);

            try
            {
                var completed = await Task.WhenAny(incomingTask, outgoingTask);
                await completed;
            }
            finally
            {
                await cts.CancelAsync();
                try { await Task.WhenAll(incomingTask, outgoingTask); }
                catch (Exception) when (cts.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Emit(LogFailedRequest, "TCP", $"{Tunnel.Host}:{Tunnel.LocalPort}");
            Emit(LogException, ex);
        }
        finally
        {
            await cts.CancelAsync();
            Emit(Log, $"[TCP] Connection {tcpConnection.RequestId} closed.");
        }
    }

    private async Task StreamIncomingAsync(TcpClient localClient, TcpConnection tcpConnection, CancellationToken cancellationToken)
    {
        try
        {
            var incomingTcpStream = Connection.StreamAsync<byte[]>("StreamIncomingAsync", tcpConnection, cancellationToken: cancellationToken);
            var localTcpStream = localClient.GetStream();

            await foreach (var chunk in incomingTcpStream.WithCancellation(cancellationToken))
            {
                await localTcpStream.WriteAsync(chunk, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex) when (ex.InnerException is SocketException) { }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested) Emit(LogException, ex);
        }
        finally
        {
            Emit(Log, $"[TCP] Writing data to connection {tcpConnection.RequestId} finished.");
        }
    }

    private async Task StreamOutgoingAsync(TcpClient localClient, TcpConnection tcpConnection, CancellationToken cancellationToken)
    {
        await Connection.InvokeAsync("StreamOutgoingAsync", StreamLocalTcpAsync(localClient, tcpConnection, cancellationToken), tcpConnection, cancellationToken: cancellationToken);
    }

    private async IAsyncEnumerable<byte[]> StreamLocalTcpAsync(TcpClient localClient, TcpConnection tcpConnection, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TunnelProtocol.ChunkSize);

        try
        {
            var tcpStream = localClient.GetStream();

            int bytesRead;
            while (!cancellationToken.IsCancellationRequested &&
                (bytesRead = await tcpStream.ReadAsync(buffer.AsMemory(0, TunnelProtocol.ChunkSize), cancellationToken)) > 0)
            {
                yield return buffer[..bytesRead];
            }
        }
        finally
        {
            Emit(Log, $"[TCP] Reading data from connection {tcpConnection.RequestId} finished.");
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<bool> ConnectWithRetryAsync(HubConnection connection, CancellationToken token)
    {
        while (true)
        {
            try
            {
                if (connection.State == HubConnectionState.Connected) return true;
                await connection.StartAsync(token);

                return true;
            }
            catch when (token.IsCancellationRequested)
            {
                return false;
            }
            catch
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return false;
                }

                Emit(LogError, $"[TCP] Cannot connect to the public server on {Tunnel.PublicUrl}");

                await Task.Delay(5000, token);
            }
        }
    }
}
