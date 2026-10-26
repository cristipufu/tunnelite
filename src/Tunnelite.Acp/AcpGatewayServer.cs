using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Tunnelite.Acp;

/// <summary>
/// Lightweight, NativeAOT-compatible loopback HTTP/1.1, WebSocket (<c>/ws</c>), and SSE (<c>/api/events</c>)
/// server that hosts the Tunnelite ACP mobile web UI and real-time agent orchestrator.
/// </summary>
public sealed class AcpGatewayServer : IAsyncDisposable
{
    private static readonly TimeSpan WebSocketKeepAliveInterval = TimeSpan.FromSeconds(25);
    private const int SseDisconnectProbeBufferSize = 256;

    private readonly AcpGatewayOrchestrator _orchestrator;
    private readonly bool _ownsOrchestrator;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, Task> _activeConnections = new();
    private Task? _acceptLoopTask;
    private long _connectionIdCounter;
    private int _isDisposed;

    public AcpGatewayServer(AcpGatewayOrchestrator orchestrator, bool ownsOrchestrator = true)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        _orchestrator = orchestrator;
        _ownsOrchestrator = ownsOrchestrator;
        _listener = new TcpListener(IPAddress.Loopback, 0);
    }

    public AcpGatewayOrchestrator Orchestrator => _orchestrator;
    public int Port { get; private set; }
    public string LocalUrl { get; private set; } = "";

    public static async Task<AcpGatewayServer> StartNewAsync(
        AcpGatewayOrchestrator orchestrator,
        bool ownsOrchestrator = true,
        CancellationToken cancellationToken = default)
    {
        var server = new AcpGatewayServer(orchestrator, ownsOrchestrator);
        await server.StartAsync(cancellationToken).ConfigureAwait(false);
        return server;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _orchestrator.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        LocalUrl = $"http://127.0.0.1:{Port}";
        _acceptLoopTask = Task.Run(() => AcceptConnectionsAsync(_lifetimeCts.Token));
    }

    private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                var connId = Interlocked.Increment(ref _connectionIdCounter);
                _activeConnections[connId] = HandleClientConnectionAsync(connId, client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
            // Listener stopped
        }
    }

    private async Task HandleClientConnectionAsync(long connId, TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            {
                await using var stream = client.GetStream();
                var request = await AcpHttpWireReader.ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                await RouteHttpRequestAsync(stream, request, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Connection closed or aborted by peer
        }
        finally
        {
            _activeConnections.TryRemove(connId, out _);
        }
    }

    private Task RouteHttpRequestAsync(
        NetworkStream stream,
        ParsedHttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Method == "GET" &&
            request.Path == "/ws" &&
            AcpHttpWireReader.TryGetWebSocketUpgradeKey(request.Headers) is { } secWebSocketKey)
        {
            return HandleWebSocketAsync(stream, secWebSocketKey, cancellationToken);
        }

        return (request.Method, request.Path) switch
        {
            ("GET", "/api/events") => HandleSseAsync(stream, cancellationToken),
            ("GET", "/" or "/index.html") => WriteStaticUtf8Async(stream, "text/html; charset=utf-8", AcpMobilePage.Html, cancellationToken),
            ("GET", "/app.js") => WriteStaticUtf8Async(stream, "text/javascript; charset=utf-8", AcpMobilePage.Script, cancellationToken),
            ("GET", "/api/state") => HandleGetStateAsync(stream, request.Query, cancellationToken),
            ("POST", "/api/action") => HandlePostActionAsync(stream, request.Body, cancellationToken),
            _ => AcpHttpWireReader.WriteHttpResponseAsync(
                stream,
                404,
                "Not Found",
                "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("Not Found"),
                cancellationToken)
        };
    }

    private static Task WriteStaticUtf8Async(
        NetworkStream stream,
        string contentType,
        string content,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return AcpHttpWireReader.WriteHttpResponseAsync(stream, 200, "OK", contentType, bytes, cancellationToken);
    }

    private async Task HandleGetStateAsync(
        NetworkStream stream,
        string query,
        CancellationToken cancellationToken)
    {
        var sessionKey = AcpHttpWireReader.TryGetQueryParam(query, "sessionKey");
        var state = await _orchestrator.GetStateSnapshotAsync(sessionKey, cancellationToken).ConfigureAwait(false);
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(state, AcpGatewayJsonContext.Default.AcpGatewayStateSnapshot);
        await AcpHttpWireReader.WriteHttpResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", jsonBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandlePostActionAsync(
        NetworkStream stream,
        byte[] bodyBytes,
        CancellationToken cancellationToken)
    {
        AcpGatewayActionRequest? actionReq = null;
        if (bodyBytes.Length > 0)
        {
            try
            {
                actionReq = JsonSerializer.Deserialize(bodyBytes, AcpGatewayJsonContext.Default.AcpGatewayActionRequest);
            }
            catch (JsonException)
            {
                var badReqBytes = Encoding.UTF8.GetBytes("{\"ok\":false,\"message\":\"Invalid JSON payload.\"}");
                await AcpHttpWireReader.WriteHttpResponseAsync(stream, 400, "Bad Request", "application/json; charset=utf-8", badReqBytes, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (actionReq is null)
        {
            var emptyBytes = Encoding.UTF8.GetBytes("{\"ok\":false,\"message\":\"Empty action payload.\"}");
            await AcpHttpWireReader.WriteHttpResponseAsync(stream, 400, "Bad Request", "application/json; charset=utf-8", emptyBytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        var response = await _orchestrator.ExecuteActionAsync(actionReq, cancellationToken).ConfigureAwait(false);
        var respBytes = JsonSerializer.SerializeToUtf8Bytes(response, AcpGatewayJsonContext.Default.AcpGatewayActionResponse);
        await AcpHttpWireReader.WriteHttpResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", respBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleWebSocketAsync(
        NetworkStream stream,
        string secWebSocketKey,
        CancellationToken cancellationToken)
    {
        await AcpHttpWireReader.WriteWebSocketHandshakeAsync(stream, secWebSocketKey, cancellationToken).ConfigureAwait(false);

        using var webSocket = WebSocket.CreateFromStream(
            stream,
            isServer: true,
            subProtocol: null,
            keepAliveInterval: WebSocketKeepAliveInterval);

        using var sendGate = new SemaphoreSlim(1, 1);

        async Task SendEnvelopeSafeAsync(AcpGatewayRealtimeEnvelope envelope)
        {
            if (webSocket.State != WebSocketState.Open)
            {
                return;
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, AcpGatewayJsonContext.Default.AcpGatewayRealtimeEnvelope);
            await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                sendGate.Release();
            }
        }

        using var sub = _orchestrator.Subscribe(SendEnvelopeSafeAsync);

        var initialState = await _orchestrator.GetStateSnapshotAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await SendEnvelopeSafeAsync(new AcpGatewayRealtimeEnvelope
        {
            Type = RealtimeEnvelopeTypes.StateSnapshot,
            State = initialState
        }).ConfigureAwait(false);

        await PumpWebSocketMessagesAsync(webSocket, SendEnvelopeSafeAsync, cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpWebSocketMessagesAsync(
        WebSocket webSocket,
        Func<AcpGatewayRealtimeEnvelope, Task> sendEnvelopeAsync,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(AcpHttpWireReader.ChunkSize);
        try
        {
            using var messageBuffer = new MemoryStream();
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(buffer.AsMemory(0, AcpHttpWireReader.ChunkSize), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken).ConfigureAwait(false);
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                messageBuffer.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    if (messageBuffer.Length > AcpHttpWireReader.MaxActionBodyBytes)
                    {
                        break;
                    }
                    continue;
                }

                var msgBytes = messageBuffer.ToArray();
                messageBuffer.SetLength(0);
                await DispatchWebSocketActionMessageAsync(msgBytes, sendEnvelopeAsync, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task DispatchWebSocketActionMessageAsync(
        byte[] msgBytes,
        Func<AcpGatewayRealtimeEnvelope, Task> sendEnvelopeAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            var req = JsonSerializer.Deserialize(msgBytes, AcpGatewayJsonContext.Default.AcpGatewayActionRequest);
            if (req is null)
            {
                return;
            }

            var actionRes = await _orchestrator.ExecuteActionAsync(req, cancellationToken).ConfigureAwait(false);
            await sendEnvelopeAsync(new AcpGatewayRealtimeEnvelope
            {
                Type = RealtimeEnvelopeTypes.ActionResult,
                SessionKey = actionRes.SessionKey,
                Session = actionRes.Session,
                State = actionRes.State,
                Message = actionRes.Message
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await sendEnvelopeAsync(new AcpGatewayRealtimeEnvelope
            {
                Type = RealtimeEnvelopeTypes.ActionError,
                Message = ex.Message
            }).ConfigureAwait(false);
        }
    }

    private async Task HandleSseAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        await AcpHttpWireReader.WriteSseHeadersAsync(stream, cancellationToken).ConfigureAwait(false);

        using var writeGate = new SemaphoreSlim(1, 1);
        var disconnectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task WriteSseEnvelopeAsync(AcpGatewayRealtimeEnvelope envelope)
        {
            var json = JsonSerializer.Serialize(envelope, AcpGatewayJsonContext.Default.AcpGatewayRealtimeEnvelope);
            var frameBytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
            await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(frameBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                disconnectTcs.TrySetResult();
                throw;
            }
            finally
            {
                writeGate.Release();
            }
        }

        using var sub = _orchestrator.Subscribe(WriteSseEnvelopeAsync);

        var initialState = await _orchestrator.GetStateSnapshotAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await WriteSseEnvelopeAsync(new AcpGatewayRealtimeEnvelope
        {
            Type = RealtimeEnvelopeTypes.StateSnapshot,
            State = initialState
        }).ConfigureAwait(false);

        await WaitForSseDisconnectAsync(stream, disconnectTcs, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WaitForSseDisconnectAsync(
        NetworkStream stream,
        TaskCompletionSource disconnectTcs,
        CancellationToken cancellationToken)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readBuffer = ArrayPool<byte>.Shared.Rent(SseDisconnectProbeBufferSize);
        Task<int>? readTask = null;
        try
        {
            using var reg = readCts.Token.Register(() => disconnectTcs.TrySetResult());
            readTask = stream.ReadAsync(readBuffer.AsMemory(0, SseDisconnectProbeBufferSize), readCts.Token).AsTask();
            await Task.WhenAny(disconnectTcs.Task, readTask).ConfigureAwait(false);
        }
        finally
        {
            await readCts.CancelAsync().ConfigureAwait(false);
            if (readTask is not null)
            {
                try
                {
                    await readTask.ConfigureAwait(false);
                }
                catch
                {
                    // Expected on cancellation/disconnect
                }
            }
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            {
                return;
            }

            await _lifetimeCts.CancelAsync().ConfigureAwait(false);
            _listener.Stop();

            await AcpManagedSession.WaitBackgroundTaskQuietlyAsync(_acceptLoopTask).ConfigureAwait(false);

            foreach (var (_, task) in _activeConnections)
            {
                await AcpManagedSession.WaitBackgroundTaskQuietlyAsync(task).ConfigureAwait(false);
            }
            _activeConnections.Clear();

            if (_ownsOrchestrator)
            {
                await _orchestrator.DisposeAsync().ConfigureAwait(false);
            }

            _lifetimeCts.Dispose();
        }
        finally
        {
            _disposeGate.Release();
        }
    }
}
