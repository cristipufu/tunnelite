using Microsoft.AspNetCore.SignalR;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace WebSocketTunnel.Server.TcpTunnel;

public class TcpTunnelHub(TcpTunnelStore tunnelStore, TcpClientStore tcpClientStore, IHubContext<TcpTunnelHub> hubContext, ILogger<TcpTunnelHub> logger) : Hub
{
    private readonly TcpTunnelStore _tunnelStore = tunnelStore;
    private readonly TcpClientStore _tcpClientStore = tcpClientStore;
    private readonly IHubContext<TcpTunnelHub> _hubContext = hubContext;
    private readonly ILogger _logger = logger;

    public override Task OnConnectedAsync()
    {
        var clientId = GetClientId(Context);

        _tunnelStore.Connections.AddOrUpdate(clientId, Context.ConnectionId, (key, oldValue) => Context.ConnectionId);

        return base.OnConnectedAsync();
    }

    public Task<TcpTunnelResponse> RegisterTunnelAsync(TcpTunnelRequest tunnel)
    {
        var response = new TcpTunnelResponse();
        var tcpListenerContext = new TcpListenerContext();

        try
        {
            tcpListenerContext.TcpListener = new TcpListener(IPAddress.Any, tunnel.PublicPort ?? 0);
            tcpListenerContext.CancellationTokenSource = new CancellationTokenSource();

            tcpListenerContext.TcpListener.Start();

            tunnel.PublicPort = ((IPEndPoint)tcpListenerContext.TcpListener.LocalEndpoint).Port;

            tcpListenerContext.AcceptConnectionsTask = AcceptConnectionsAsync(
                tcpListenerContext.TcpListener,
                tunnel.ClientId,
                tcpListenerContext.CancellationTokenSource.Token);

            _tcpClientStore.AddTcpListener(tunnel.ClientId, tcpListenerContext);

            var httpContext = Context.GetHttpContext();
            var tunnelUrl = $"tcp://{httpContext!.Request.Host.Host}:{tunnel.PublicPort}";

            response.Port = tunnel.PublicPort ?? 0;
            response.TunnelUrl = tunnelUrl;
        }
        catch (Exception ex)
        {
            response.Message = "An error occurred while creating the tunnel";
            response.Error = ex.Message;

            tcpListenerContext.Dispose();
        }

        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamIncomingAsync(TcpConnection tcpConnection)
    {
        var clientId = GetClientId(Context);

        var tcpClient = _tcpClientStore.GetTcpClient(clientId, tcpConnection.RequestId);

        if (tcpClient == null)
        {
            yield break;
        }

        const int chunkSize = 32 * 1024;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkSize);

        try
        {
            var stream = tcpClient.GetStream();

            int bytesRead;

            while (!Context.ConnectionAborted.IsCancellationRequested &&
                (bytesRead = await stream.ReadAsync(buffer, Context.ConnectionAborted)) > 0)
            {
                yield return new ReadOnlyMemory<byte>(buffer, 0, bytesRead);
            }
        }
        finally
        {
            _logger.LogInformation("Done reading.. Closing TCP client {RequestId}", tcpConnection.RequestId);

            ArrayPool<byte>.Shared.Return(buffer);

            _tcpClientStore.DisposeTcpClient(clientId, tcpConnection.RequestId);
        }
    }

    public async Task StreamOutgoingAsync(TcpConnection tcpConnection, IAsyncEnumerable<ReadOnlyMemory<byte>> stream)
    {
        var clientId = GetClientId(Context);

        var tcpClient = _tcpClientStore.GetTcpClient(clientId, tcpConnection.RequestId);

        if (tcpClient == null)
        {
            return;
        }

        try
        {
            var tcpStream = tcpClient.GetStream();

            await foreach (var chunk in stream.WithCancellation(Context.ConnectionAborted))
            {
                await tcpStream.WriteAsync(chunk, Context.ConnectionAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (ChannelClosedException)
        {
            // ignore
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            // ignore
        }
        catch (Exception ex) when (ex.Message == "Stream canceled by client.")
        {
            // ignore
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred while streaming outgoing data for {RequestId}", tcpConnection.RequestId);
        }
        finally
        {
            _logger.LogInformation("Done writing.. TCP client {RequestId}", tcpConnection.RequestId);

            _tcpClientStore.DisposeTcpClient(clientId, tcpConnection.RequestId);
        }
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var clientId = GetClientId(Context);

        _tunnelStore.Connections.Remove(clientId, out var _);

        _tcpClientStore.DisposeTcpListener(clientId);

        return base.OnDisconnectedAsync(exception);
    }

    private async Task AcceptConnectionsAsync(TcpListener listener, Guid clientId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var tcpClient = await listener.AcceptTcpClientAsync(cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    tcpClient.Dispose();

                    break;
                }

                if (!_tunnelStore.Connections.TryGetValue(clientId, out var connectionId))
                {
                    continue;
                }

                var tcpConnection = new TcpConnection
                {
                    RequestId = Guid.NewGuid(),
                };

                _logger.LogInformation("New TCP client connected {RequestId}", tcpConnection.RequestId);

                _tcpClientStore.AddTcpClient(clientId, tcpConnection.RequestId, tcpClient);

                await _hubContext.Clients.Client(connectionId).SendAsync("NewTcpConnection", tcpConnection, cancellationToken: cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected, tcp listener disposed
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error has occurred while listening for incoming TCP connections: {Message}", ex.Message);

            _tcpClientStore.DisposeTcpListener(clientId);

            if (_tunnelStore.Connections.TryGetValue(clientId, out var connectionId))
            {
                await _hubContext.Clients.Client(connectionId).SendAsync("TcpTunnelClosed", ex.Message, cancellationToken: cancellationToken);
            }
        }
    }

    private static Guid GetClientId(HubCallerContext context)
    {
        return Guid.Parse(context.GetHttpContext()!.Request.Query["clientId"].ToString());
    }
}