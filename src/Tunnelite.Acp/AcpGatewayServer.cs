using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tunnelite.Acp;

/// <summary>
/// Lightweight, NativeAOT-compatible loopback HTTP/1.1, WebSocket (<c>/ws</c>), and SSE (<c>/api/events</c>)
/// server that hosts the Tunnelite ACP mobile web UI and real-time agent orchestrator.
/// </summary>
public sealed class AcpGatewayServer : IAsyncDisposable
{
    private const int ChunkSize = 8192;
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxActionBodyBytes = 256 * 1024;
    private const string WebSocketAcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

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
                var task = HandleClientConnectionAsync(connId, client, cancellationToken);
                _activeConnections[connId] = task;
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
                var parsed = await ReadHttpRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                if (parsed is null)
                {
                    return;
                }

                var (method, rawTarget, headers, bodyBytes) = parsed.Value;
                var (path, query) = SplitPathAndQuery(rawTarget);

                if (method == "GET" && path == "/ws" && IsWebSocketUpgrade(headers, out var secWebSocketKey))
                {
                    await HandleWebSocketAsync(stream, secWebSocketKey, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (method == "GET" && path == "/api/events")
                {
                    await HandleSseAsync(stream, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (method == "GET" && (path == "/" || path == "/index.html"))
                {
                    var htmlBytes = Encoding.UTF8.GetBytes(AcpMobilePage.Html);
                    await WriteHttpResponseAsync(stream, 200, "OK", "text/html; charset=utf-8", htmlBytes, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (method == "GET" && path == "/app.js")
                {
                    var jsBytes = Encoding.UTF8.GetBytes(AcpMobilePage.Script);
                    await WriteHttpResponseAsync(stream, 200, "OK", "text/javascript; charset=utf-8", jsBytes, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (method == "GET" && path == "/api/state")
                {
                    var sessionKey = TryGetQueryParam(query, "sessionKey");
                    var state = await _orchestrator.GetStateSnapshotAsync(sessionKey, cancellationToken).ConfigureAwait(false);
                    var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(state, AcpGatewayJsonContext.Default.AcpGatewayStateSnapshot);
                    await WriteHttpResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", jsonBytes, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (method == "POST" && path == "/api/action")
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
                            await WriteHttpResponseAsync(stream, 400, "Bad Request", "application/json; charset=utf-8", badReqBytes, cancellationToken).ConfigureAwait(false);
                            return;
                        }
                    }

                    if (actionReq is null)
                    {
                        var emptyBytes = Encoding.UTF8.GetBytes("{\"ok\":false,\"message\":\"Empty action payload.\"}");
                        await WriteHttpResponseAsync(stream, 400, "Bad Request", "application/json; charset=utf-8", emptyBytes, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    var response = await _orchestrator.ExecuteActionAsync(actionReq, cancellationToken).ConfigureAwait(false);
                    var respBytes = JsonSerializer.SerializeToUtf8Bytes(response, AcpGatewayJsonContext.Default.AcpGatewayActionResponse);
                    await WriteHttpResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", respBytes, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var notFoundBytes = Encoding.UTF8.GetBytes("Not Found");
                await WriteHttpResponseAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", notFoundBytes, cancellationToken).ConfigureAwait(false);
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

    private async Task HandleWebSocketAsync(
        NetworkStream stream,
        string secWebSocketKey,
        CancellationToken cancellationToken)
    {
        var acceptHash = SHA1.HashData(Encoding.ASCII.GetBytes(secWebSocketKey.Trim() + WebSocketAcceptGuid));
        var acceptKey = Convert.ToBase64String(acceptHash);

        var handshake =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";

        var handshakeBytes = Encoding.ASCII.GetBytes(handshake);
        await stream.WriteAsync(handshakeBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        using var webSocket = WebSocket.CreateFromStream(
            stream,
            isServer: true,
            subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(25));

        var sendGate = new SemaphoreSlim(1, 1);

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
            Type = "state_snapshot",
            State = initialState
        }).ConfigureAwait(false);

        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            using var messageBuffer = new MemoryStream();
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(buffer.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false);
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
                    if (messageBuffer.Length > MaxActionBodyBytes)
                    {
                        break;
                    }
                    continue;
                }

                var msgBytes = messageBuffer.ToArray();
                messageBuffer.SetLength(0);

                try
                {
                    var req = JsonSerializer.Deserialize(msgBytes, AcpGatewayJsonContext.Default.AcpGatewayActionRequest);
                    if (req is not null)
                    {
                        var actionRes = await _orchestrator.ExecuteActionAsync(req, cancellationToken).ConfigureAwait(false);
                        await SendEnvelopeSafeAsync(new AcpGatewayRealtimeEnvelope
                        {
                            Type = "action_result",
                            SessionKey = actionRes.SessionKey,
                            Session = actionRes.Session,
                            State = actionRes.State,
                            Message = actionRes.Message
                        }).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    await SendEnvelopeSafeAsync(new AcpGatewayRealtimeEnvelope
                    {
                        Type = "action_error",
                        Message = ex.Message
                    }).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task HandleSseAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var headers =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/event-stream; charset=utf-8\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";

        var headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var writeGate = new SemaphoreSlim(1, 1);
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
            Type = "state_snapshot",
            State = initialState
        }).ConfigureAwait(false);

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readBuffer = ArrayPool<byte>.Shared.Rent(256);
        Task<int>? readTask = null;
        try
        {
            using var reg = readCts.Token.Register(() => disconnectTcs.TrySetResult());
            readTask = stream.ReadAsync(readBuffer.AsMemory(0, 256), readCts.Token).AsTask();
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

    private static async Task<(string Method, string Target, Dictionary<string, string> Headers, byte[] Body)?> ReadHttpRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            using var headerStream = new MemoryStream();
            var headerEndIndex = -1;
            var totalRead = 0;

            while (headerEndIndex < 0 && totalRead < MaxHeaderBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                headerStream.Write(buffer, 0, read);
                totalRead += read;

                var currentBytes = headerStream.GetBuffer();
                headerEndIndex = FindHeaderDelimiter(currentBytes.AsSpan(0, (int)headerStream.Length));
            }

            if (headerEndIndex < 0)
            {
                return null;
            }

            var allAccumulated = headerStream.ToArray();
            var headerText = Encoding.ASCII.GetString(allAccumulated, 0, headerEndIndex);
            var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                return null;
            }

            var requestLineParts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (requestLineParts.Length < 2)
            {
                return null;
            }

            var method = requestLineParts[0].ToUpperInvariant();
            var target = requestLineParts[1];
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon > 0)
                {
                    var name = lines[i][..colon].Trim();
                    var value = lines[i][(colon + 1)..].Trim();
                    headers[name] = value;
                }
            }

            var bodyStartOffset = headerEndIndex + 4;
            var isChunked = headers.TryGetValue("Transfer-Encoding", out var teRaw) &&
                            teRaw.Contains("chunked", StringComparison.OrdinalIgnoreCase);

            if (isChunked)
            {
                var body = await ReadChunkedBodyAsync(
                    stream,
                    allAccumulated,
                    bodyStartOffset,
                    buffer,
                    cancellationToken).ConfigureAwait(false);
                return (method, target, headers, body);
            }

            var contentLength = 0;
            if (headers.TryGetValue("Content-Length", out var clRaw) && int.TryParse(clRaw, out var parsedLen))
            {
                contentLength = Math.Clamp(parsedLen, 0, MaxActionBodyBytes);
            }

            var alreadyReadBodyBytes = allAccumulated.Length - bodyStartOffset;
            if (contentLength <= 0)
            {
                return (method, target, headers, []);
            }

            var contentBody = new byte[contentLength];
            var copied = Math.Min(contentLength, Math.Max(0, alreadyReadBodyBytes));
            if (copied > 0)
            {
                Buffer.BlockCopy(allAccumulated, bodyStartOffset, contentBody, 0, copied);
            }

            var remaining = contentLength - copied;
            var offset = copied;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(contentBody.AsMemory(offset, remaining), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                offset += read;
                remaining -= read;
            }

            return (method, target, headers, contentBody);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(
        NetworkStream stream,
        byte[] initialBuffer,
        int initialOffset,
        byte[] scratchBuffer,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var tail = new List<byte>(Math.Max(256, initialBuffer.Length - initialOffset));
        for (var i = initialOffset; i < initialBuffer.Length; i++)
        {
            tail.Add(initialBuffer[i]);
        }
        var readPos = 0;

        void CompactIfConsumed()
        {
            if (readPos > 0 && readPos == tail.Count)
            {
                tail.Clear();
                readPos = 0;
            }
            else if (readPos >= 4096 && readPos >= tail.Count / 2)
            {
                tail.RemoveRange(0, readPos);
                readPos = 0;
            }
        }

        async Task<bool> EnsureBytesAsync(int needed)
        {
            while (tail.Count - readPos < needed)
            {
                var read = await stream.ReadAsync(scratchBuffer.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return false;
                }
                for (var i = 0; i < read; i++)
                {
                    tail.Add(scratchBuffer[i]);
                }
            }
            return true;
        }

        async Task<string?> ReadLineCrLfAsync()
        {
            while (true)
            {
                for (var i = readPos; i <= tail.Count - 2; i++)
                {
                    if (tail[i] == (byte)'\r' && tail[i + 1] == (byte)'\n')
                    {
                        var len = i - readPos;
                        var chars = new char[len];
                        for (var c = 0; c < len; c++)
                        {
                            chars[c] = (char)tail[readPos + c];
                        }
                        readPos = i + 2;
                        CompactIfConsumed();
                        return new string(chars);
                    }
                }

                var read = await stream.ReadAsync(scratchBuffer.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }
                for (var i = 0; i < read; i++)
                {
                    tail.Add(scratchBuffer[i]);
                }
            }
        }

        while (output.Length < MaxActionBodyBytes)
        {
            var sizeLine = await ReadLineCrLfAsync().ConfigureAwait(false);
            if (sizeLine is null)
            {
                break;
            }

            var semi = sizeLine.IndexOf(';');
            var hexPart = (semi >= 0 ? sizeLine[..semi] : sizeLine).Trim();
            if (!int.TryParse(hexPart, System.Globalization.NumberStyles.HexNumber, null, out var chunkLen) || chunkLen < 0)
            {
                break;
            }

            if (chunkLen == 0)
            {
                // Consume trailing CRLF
                await ReadLineCrLfAsync().ConfigureAwait(false);
                break;
            }

            if (!await EnsureBytesAsync(chunkLen + 2).ConfigureAwait(false))
            {
                break;
            }

            for (var i = 0; i < chunkLen && output.Length < MaxActionBodyBytes; i++)
            {
                output.WriteByte(tail[readPos + i]);
            }
            readPos += chunkLen + 2;
            CompactIfConsumed();
        }

        return output.ToArray();
    }

    private static int FindHeaderDelimiter(ReadOnlySpan<byte> span)
    {
        for (var i = 0; i <= span.Length - 4; i++)
        {
            if (span[i] == (byte)'\r' &&
                span[i + 1] == (byte)'\n' &&
                span[i + 2] == (byte)'\r' &&
                span[i + 3] == (byte)'\n')
            {
                return i;
            }
        }
        return -1;
    }

    private static bool IsWebSocketUpgrade(Dictionary<string, string> headers, out string secWebSocketKey)
    {
        secWebSocketKey = string.Empty;
        if (!headers.TryGetValue("Upgrade", out var upgrade) ||
            !upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key) || string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        secWebSocketKey = key;
        return true;
    }

    private static async Task WriteHttpResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reasonPhrase,
        string contentType,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var header =
            $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; img-src 'self' data:; connect-src 'self' ws: wss:; base-uri 'none'; frame-ancestors 'none'\r\n" +
            "Connection: close\r\n\r\n";

        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static (string Path, string Query) SplitPathAndQuery(string rawTarget)
    {
        var qIndex = rawTarget.IndexOf('?');
        return qIndex < 0
            ? (rawTarget, string.Empty)
            : (rawTarget[..qIndex], rawTarget[(qIndex + 1)..]);
    }

    private static string? TryGetQueryParam(string query, string key)
    {
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && string.Equals(part[..eq], key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }
        return null;
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

            if (_acceptLoopTask is not null)
            {
                try
                {
                    await _acceptLoopTask.WaitAsync(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore
                }
            }

            foreach (var (_, task) in _activeConnections)
            {
                try
                {
                    await task.WaitAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore
                }
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
