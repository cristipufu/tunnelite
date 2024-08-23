using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Tunnelite.Client.TcpTunnel;

public class TcpTunnelClient
{
    private readonly HubConnection Connection;
    private readonly TcpTunnelRequest Tunnel;
    private TcpTunnelResponse? _currentTunnel = null;

    public TcpTunnelClient(TcpTunnelRequest tunnel, LogLevel logLevel)
    {
        Tunnel = tunnel;

        Connection = new HubConnectionBuilder()
            .WithUrl($"{Tunnel.PublicUrl}/wsstcptunnel?clientId={tunnel.ClientId}")
            .AddMessagePackProtocol()
            .ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(logLevel);
                logging.AddConsole();
            })
            .WithAutomaticReconnect()
            .Build();

        Connection.On<TcpConnection>("NewTcpConnection", (tcpConnection) =>
        {
            Console.WriteLine($"New TCP Connection {tcpConnection.RequestId}");

            _ = HandleNewTcpConnectionAsync(tcpConnection);

            return Task.CompletedTask;
        });

        Connection.On<string>("TcpTunnelClosed", async (errorMessage) =>
        {
            Console.WriteLine($"TCP Tunnel closed by server: {errorMessage}");

            _currentTunnel = await RegisterTunnelAsync(tunnel);
        });

        Connection.Reconnected += async connectionId =>
        {
            Console.WriteLine($"Reconnected. New ConnectionId {connectionId}");

            _currentTunnel = await RegisterTunnelAsync(tunnel);
        };

        Connection.Closed += async (error) =>
        {
            Console.WriteLine("Connection closed... reconnecting");

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

                if (string.IsNullOrEmpty(tunnelResponse.Error))
                {
                    Console.WriteLine($"Tunnel created successfully: {tunnelResponse!.TunnelUrl}");
                }
                else
                {
                    Console.WriteLine($"{tunnelResponse!.Message}:{tunnelResponse.Error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred while registering the tunnel {ex.Message}");

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
            Console.WriteLine($"Error handling TCP connection {ex.Message}");
        }
        finally
        {
            cts.Cancel();

            Console.WriteLine($"TCP Connection {tcpConnection.RequestId} done.");
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
            Console.WriteLine($"Writing data to TCP connection {tcpConnection.RequestId} finished.");
        }
    }

    private async Task StreamOutgoingAsync(TcpClient localClient, TcpConnection tcpConnection, CancellationToken cancellationToken)
    {
        await Connection.InvokeAsync("StreamOutgoingAsync", StreamLocalTcpAsync(localClient, tcpConnection, cancellationToken), tcpConnection, cancellationToken: cancellationToken);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamLocalTcpAsync(TcpClient localClient, TcpConnection tcpConnection, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int chunkSize = 32 * 1024;

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
            Console.WriteLine($"Reading data from TCP connection {tcpConnection.RequestId} finished.");

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

                Console.WriteLine($"Client connected to SignalR hub. ConnectionId: {connection.ConnectionId}");

                return true;
            }
            catch when (token.IsCancellationRequested)
            {
                return false;
            }
            catch
            {
                Console.WriteLine($"Cannot connect to WebSocket server on {Tunnel.PublicUrl}");

                await Task.Delay(5000, token);
            }
        }
    }
}
