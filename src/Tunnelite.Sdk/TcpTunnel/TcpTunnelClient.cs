using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Tunnelite.Sdk;

public class TcpTunnelClient : ITunnelClient
{
    public event Func<Task>? Connected;
    public event Action<string, string>? LogRequest;
    public event Action<string, string>? LogFailedRequest;
    public event Action<Exception>? LogException;
    public event Action<string>? Log;
    public event Action<string>? LogError;

    public HubConnection Connection { get; }

    public string? TunnelUrl
    {
        get
        {
            return _currentTunnel?.TunnelUrl;
        }
    }

    private readonly TcpTunnelRequest Tunnel;
    private TcpTunnelResponse? _currentTunnel = null;

    public TcpTunnelClient(TcpTunnelRequest tunnel, LogLevel? logLevel)
    {
        Tunnel = tunnel;

        Connection = new HubConnectionBuilder()
            .WithUrl($"{Tunnel.PublicUrl}/wsstcptunnel?clientId={tunnel.ClientId}")
            .AddMessagePackProtocol()
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

        Connection.On<TcpConnection>("NewTcpConnection", (tcpConnection) =>
        {
            LogRequest?.Invoke("TCP", $"New Connection {tcpConnection.RequestId}");

            _ = HandleNewTcpConnectionAsync(tcpConnection);

            return Task.CompletedTask;
        });

        Connection.On<string>("TcpTunnelClosed", async (errorMessage) =>
        {
            LogError?.Invoke($"[TCP] Tunnel closed by server: {errorMessage}.");

            _currentTunnel = await RegisterTunnelAsync(tunnel);
        });

        Connection.Reconnected += async connectionId =>
        {
            _currentTunnel = await RegisterTunnelAsync(tunnel);
        };

        Connection.Closed += async (error) =>
        {
            await Task.Delay(new Random().Next(0, 5) * 1000);

            if (await ConnectWithRetryAsync(Connection, CancellationToken.None))
            {
                _currentTunnel = await RegisterTunnelAsync(tunnel);
            }
        };
    }

    public async Task ConnectAsync()
    {
        if (await ConnectWithRetryAsync(Connection, CancellationToken.None))
        {
            _currentTunnel = await RegisterTunnelAsync(Tunnel);
        }
    }

    public async Task<TcpTunnelResponse?> RegisterTunnelAsync(TcpTunnelRequest tunnel)
    {
        tunnel.PublicPort = _currentTunnel?.Port;

        TcpTunnelResponse? tunnelResponse = null;

        while (tunnelResponse == null)
        {
            try
            {
                tunnelResponse = await Connection.InvokeAsync<TcpTunnelResponse>("RegisterTunnelAsync", tunnel);

                if (!string.IsNullOrEmpty(tunnelResponse.Error))
                {
                    LogError?.Invoke($"[TCP] {tunnelResponse!.Message}:{tunnelResponse.Error}");
                }
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[TCP] An error occurred while registering the tunnel:");
                LogException?.Invoke(ex);

                await Task.Delay(5000);
            }
        }

        return tunnelResponse;
    }

    private async Task HandleNewTcpConnectionAsync(TcpConnection tcpConnection)
    {
        using var localClient = new TcpClient();
        using var cts = new CancellationTokenSource();

        try
        {
            await localClient.ConnectAsync(Tunnel.Host, Tunnel.LocalPort);

            var incomingTask = StreamIncomingAsync(localClient, tcpConnection, cts.Token);
            var outgoingTask = StreamOutgoingAsync(localClient, tcpConnection, cts.Token);

            await Task.WhenAny(incomingTask, outgoingTask);
        }
        catch (Exception ex)
        {
            LogException?.Invoke(ex);
        }
        finally
        {
            await cts.CancelAsync();

            Log?.Invoke($"[TCP] Connection {tcpConnection.RequestId} closed.");
        }
    }

    private async Task StreamIncomingAsync(TcpClient localClient, TcpConnection tcpConnection, CancellationToken cancellationToken)
    {
        try
        {
            var incomingTcpStream = Connection.StreamAsync<ReadOnlyMemory<byte>>("StreamIncomingAsync", tcpConnection, cancellationToken: cancellationToken);

            var localTcpStream = localClient.GetStream();

            await foreach (var chunk in incomingTcpStream.WithCancellation(cancellationToken))
            {
                await localTcpStream.WriteAsync(chunk, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            // ignore
        }
        catch (Exception)
        {
            // ignore
        }
        finally
        {
            Log?.Invoke($"[TCP] Writing data to connection {tcpConnection.RequestId} finished.");
        }
    }

    private async Task StreamOutgoingAsync(TcpClient localClient, TcpConnection tcpConnection, CancellationToken cancellationToken)
    {
        await Connection.InvokeAsync("StreamOutgoingAsync", StreamLocalTcpAsync(localClient, tcpConnection, cancellationToken), tcpConnection, cancellationToken: cancellationToken);
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamLocalTcpAsync(TcpClient localClient, TcpConnection tcpConnection, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int chunkSize = 16 * 1024;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkSize);

        try
        {
            var tcpStream = localClient.GetStream();

            int bytesRead;
            while (!cancellationToken.IsCancellationRequested &&
                (bytesRead = await tcpStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                yield return new ReadOnlyMemory<byte>(buffer, 0, bytesRead);
            }
        }
        finally
        {
            Log?.Invoke($"[TCP] Reading data from connection {tcpConnection.RequestId} finished.");

            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<bool> ConnectWithRetryAsync(HubConnection connection, CancellationToken token)
    {
        while (true)
        {
            try
            {
                await connection.StartAsync(token);

                return true;
            }
            catch when (token.IsCancellationRequested)
            {
                return false;
            }
            catch
            {
                LogError?.Invoke($"[TCP] Cannot connect to the public server on {Tunnel.PublicUrl}");

                await Task.Delay(5000, token);
            }
        }
    }
}
