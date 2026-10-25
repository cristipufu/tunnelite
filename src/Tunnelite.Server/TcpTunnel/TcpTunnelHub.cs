using Microsoft.AspNetCore.SignalR;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Tunnelite.Server.TcpTunnel;

public class TcpTunnelHub(TcpTunnelStore tunnelStore, TcpClientStore tcpClientStore, IHubContext<TcpTunnelHub> hubContext, ILogger<TcpTunnelHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var clientId = GetClientId(Context);

        await tcpClientStore.SetActiveConnectionAsync(tunnelStore, clientId, Context.ConnectionId, Context.ConnectionAborted);

        await base.OnConnectedAsync();
    }

    public async Task<TcpTunnelResponse> RegisterTunnelAsync(TcpTunnelRequest tunnel)
    {
        var response = new TcpTunnelResponse();
        var clientId = GetClientId(Context);
        if (clientId == Guid.Empty || (tunnel.ClientId != Guid.Empty && tunnel.ClientId != clientId))
        {
            response.Message = "An error occurred while creating the tunnel";
            response.Error = "Missing or invalid 'ClientId' property.";
            return response;
        }
        if (tunnel.PublicPort is < 0 or > 65535)
        {
            response.Message = "An error occurred while creating the tunnel";
            response.Error = "Invalid 'PublicPort' property.";
            return response;
        }
        tunnel.ClientId = clientId;

        var tcpListenerContext = new TcpListenerContext
        {
            ConnectionId = Context.ConnectionId,
        };

        try
        {
            if (Context.ConnectionAborted.IsCancellationRequested
                || !await tcpClientStore.TryBeginRegistrationAsync(tunnelStore, clientId, Context.ConnectionId, Context.ConnectionAborted))
            {
                response.Message = "An error occurred while creating the tunnel";
                response.Error = "The tunnel connection is no longer active.";
                return response;
            }

            tcpListenerContext.CancellationTokenSource = new CancellationTokenSource();
            tcpListenerContext.TcpListener = StartListenerWithFallback(tunnel.PublicPort ?? 0);

            tunnel.PublicPort = ((IPEndPoint)tcpListenerContext.TcpListener.LocalEndpoint).Port;

            if (Context.ConnectionAborted.IsCancellationRequested
                || !await tcpClientStore.TryCompleteRegistrationAsync(tunnelStore, clientId, Context.ConnectionId, tcpListenerContext, Context.ConnectionAborted))
            {
                await tcpListenerContext.DisposeAsync();
                response.Message = "An error occurred while creating the tunnel";
                response.Error = "The tunnel connection is no longer active.";
                return response;
            }

            tcpListenerContext.AcceptConnectionsTask = AcceptConnectionsAsync(
                tcpListenerContext,
                clientId,
                tcpListenerContext.CancellationTokenSource.Token);

            var httpContext = Context.GetHttpContext();
            var tunnelUrl = $"tcp://{httpContext!.Request.Host.Host}:{tunnel.PublicPort}";

            response.Port = tunnel.PublicPort ?? 0;
            response.TunnelUrl = tunnelUrl;
        }
        catch (Exception ex)
        {
            response.Message = "An error occurred while creating the tunnel";
            response.Error = ex.Message;

            await tcpClientStore.DisposeTcpListenerAsync(tunnelStore, clientId, tcpListenerContext);
        }

        return response;
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamIncomingAsync(TcpConnection tcpConnection)
    {
        var clientId = GetClientId(Context);

        var tcpClient = tcpClientStore.GetTcpClient(clientId, tcpConnection.RequestId);

        if (tcpClient == null)
        {
            yield break;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(TunnelProtocol.ChunkSize);

        int bytesRead;

        try
        {
            var stream = tcpClient.GetStream();


            while (!Context.ConnectionAborted.IsCancellationRequested &&
                (bytesRead = await stream.ReadAsync(buffer.AsMemory(0, TunnelProtocol.ChunkSize), Context.ConnectionAborted)) > 0)
            {
                yield return new ReadOnlyMemory<byte>(buffer, 0, bytesRead);
            }
        }
        finally
        {
            logger.LogInformation("Done reading.. Closing TCP client {RequestId}", tcpConnection.RequestId);

            ArrayPool<byte>.Shared.Return(buffer);

            tcpClientStore.DisposeTcpClient(clientId, tcpConnection.RequestId);
        }
    }

    public async Task StreamOutgoingAsync(TcpConnection tcpConnection, IAsyncEnumerable<ReadOnlyMemory<byte>> stream)
    {
        var clientId = GetClientId(Context);

        var tcpClient = tcpClientStore.GetTcpClient(clientId, tcpConnection.RequestId);

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
            logger.LogError(ex, "An unexpected error occurred while streaming outgoing data for {RequestId}", tcpConnection.RequestId);
        }
        finally
        {
            logger.LogInformation("Done writing.. TCP client {RequestId}", tcpConnection.RequestId);

            tcpClientStore.DisposeTcpClient(clientId, tcpConnection.RequestId);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var clientId = GetClientId(Context);

        await tcpClientStore.DisconnectAsync(tunnelStore, clientId, Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
    }

    private async Task AcceptConnectionsAsync(TcpListenerContext listenerContext, Guid clientId, CancellationToken cancellationToken)
    {
        var listener = listenerContext.TcpListener;
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

                if (!tunnelStore.Connections.TryGetValue(clientId, out var connectionId))
                {
                    tcpClient.Dispose();
                    continue;
                }

                var tcpConnection = new TcpConnection
                {
                    RequestId = Guid.NewGuid(),
                };

                logger.LogInformation("New TCP client connected {RequestId}", tcpConnection.RequestId);

                tcpClientStore.AddTcpClient(clientId, tcpConnection.RequestId, tcpClient);

                await hubContext.Clients.Client(connectionId).SendAsync("NewTcpConnection", tcpConnection, cancellationToken: cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected, tcp listener disposed
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // listener disposed while accepting
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error has occurred while listening for incoming TCP connections: {Message}", ex.Message);

            await tcpClientStore.DisposeTcpListenerAsync(tunnelStore, clientId, listenerContext);

            if (tunnelStore.Connections.TryGetValue(clientId, out var connectionId))
            {
                await hubContext.Clients.Client(connectionId).SendAsync("TcpTunnelClosed", ex.Message, cancellationToken: CancellationToken.None);
            }
        }
    }

    private static TcpListener StartListenerWithFallback(int preferredPort)
    {
        var listener = new TcpListener(IPAddress.Any, preferredPort);
        try
        {
            listener.Start();
            return listener;
        }
        catch (SocketException) when (preferredPort > 0)
        {
            listener.Dispose();
            var fallback = new TcpListener(IPAddress.Any, 0);
            fallback.Start();
            return fallback;
        }
    }

    private static Guid GetClientId(HubCallerContext context) =>
        Guid.TryParse(context.GetHttpContext()?.Request.Query["clientId"], out var clientId) ? clientId : Guid.Empty;
}
