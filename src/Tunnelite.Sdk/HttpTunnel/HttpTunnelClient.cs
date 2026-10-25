using Microsoft.AspNetCore.SignalR.Client;
using Nerdbank.MessagePack;
using Nerdbank.MessagePack.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using Tunnelite.Protocol;

namespace Tunnelite.Sdk;

public class HttpTunnelClient : ITunnelClient, IAsyncDisposable
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

    private static readonly HttpClientHandler LocalHttpClientHandler = new()
    {
        UseCookies = false,
        AllowAutoRedirect = false,
    };
    private static readonly HttpClient ServerHttpClient = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private static readonly HttpClient LocalHttpClient = new(LocalHttpClientHandler);
    private readonly HttpClient _localHttpClient;

    private HttpTunnelResponse? _currentTunnel = null;
    private readonly HttpTunnelRequest Tunnel;
    private int _disposed;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<long, Task> _requests = new();
    private long _operationId;

    public HttpTunnelClient(HttpTunnelRequest tunnel, LogLevel? logLevel)
    {
        ArgumentNullException.ThrowIfNull(tunnel);
        if (!Uri.TryCreate(tunnel.LocalUrl, UriKind.Absolute, out var local)
            || local.Scheme is not ("http" or "https") || local.Query.Length > 0 || local.Fragment.Length > 0)
            throw new ArgumentException("The local URL must be an HTTP(S) base URL without a query or fragment.", nameof(tunnel));
        if (!Uri.TryCreate(tunnel.PublicUrl, UriKind.Absolute, out var relayUri)
            || relayUri.Scheme is not ("http" or "https") || relayUri.Query.Length > 0 || relayUri.Fragment.Length > 0)
            throw new ArgumentException("The relay must be an HTTP(S) base URL without a query or fragment.", nameof(tunnel));
        if (tunnel.AllowUntrustedLocalCertificates == true && !local.IsLoopback)
            throw new ArgumentException("Untrusted local certificates are allowed only on loopback.", nameof(tunnel));
        tunnel.LocalUrl = local.AbsoluteUri.TrimEnd('/');
        tunnel.PublicUrl = relayUri.AbsoluteUri.TrimEnd('/');
        tunnel.ClientId ??= Guid.NewGuid();
        if (tunnel.ClientId == Guid.Empty) throw new ArgumentException("A client id cannot be empty.", nameof(tunnel));
        var allowUntrusted = tunnel.AllowUntrustedLocalCertificates ?? (tunnel.Access is null);
        _localHttpClient = allowUntrusted ? new HttpClient(new HttpClientHandler
        {
            UseCookies = false, AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        }) : LocalHttpClient;
        Tunnel = tunnel;
        if (tunnel.Access is not null)
        {
            if (tunnel.OwnerToken is not null && tunnel.OwnerToken != tunnel.Access.Identity.Secret)
                throw new ArgumentException("The pairing policy and tunnel must use the same desktop identity.", nameof(tunnel));
            tunnel.OwnerToken = tunnel.Access.Identity.Secret;
        }
        if (tunnel.OwnerToken is not null)
        {
            if (!Uri.TryCreate(tunnel.PublicUrl, UriKind.Absolute, out var relay)
                || (relay.Scheme != "https" && !(relay.Scheme == "http" && relay.IsLoopback)))
                throw new ArgumentException("Protected tunnels require an HTTPS relay (HTTP is allowed for loopback tests).", nameof(tunnel));
            var identity = new TunnelIdentity(tunnel.OwnerToken);
            tunnel.ClientId = identity.ClientId;
            tunnel.Subdomain = identity.Subdomain;
        }

        Connection = new HubConnectionBuilder()
            .WithUrl($"{tunnel.PublicUrl}/wsshttptunnel?clientId={tunnel.ClientId}", options =>
                options.AccessTokenProvider = () => Task.FromResult<string?>(tunnel.OwnerToken))
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

        Connection.On<HttpConnection>("NewHttpConnection", (httpConnection) =>
        {
            LogRequest?.Invoke(httpConnection.Method, httpConnection.Path);

            Track(() => TunnelHttpConnectionAsync(httpConnection));

            return Task.CompletedTask;
        });

        Connection.On<WsConnection>("NewWsConnection", (wsConnection) =>
        {
            LogRequest?.Invoke("WS", wsConnection.Path);

            Track(() => TunnelWsConnectionAsync(wsConnection));

            return Task.CompletedTask;
        });

        Connection.On<SseConnection>("NewSseConnection", (sseConnection) =>
        {
            LogRequest?.Invoke("SSE", sseConnection.Path);

            Track(() => TunnelSseConnectionAsync(sseConnection));

            return Task.CompletedTask;
        });

        Connection.Reconnecting += _ => { _currentTunnel = null; return Task.CompletedTask; };
        Connection.Reconnected += _ => ObserveAsync(async () =>
        {
            _currentTunnel = await RegisterTunnelAsync(tunnel, _lifetime.Token);
            await NotifyReadyAsync();
        });
        Connection.Closed += _ => ObserveAsync(async () =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _currentTunnel = null;
            await Task.Delay(Random.Shared.Next(0, 5000), _lifetime.Token);
            if (await ConnectWithRetryAsync(Connection, _lifetime.Token))
            {
                _currentTunnel = await RegisterTunnelAsync(tunnel, _lifetime.Token);
                await NotifyReadyAsync();
            }
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
        await Task.WhenAll(_requests.Values);
        if (!ReferenceEquals(_localHttpClient, LocalHttpClient)) _localHttpClient.Dispose();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    public Task ConnectAsync() => ConnectAsync(CancellationToken.None);

    /// <summary>Connects and registers the endpoint. Cancellation also stops initial retry attempts.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        if (await ConnectWithRetryAsync(Connection, attempt.Token))
        {
            _currentTunnel = await RegisterTunnelAsync(Tunnel, attempt.Token);
            await NotifyReadyAsync();
        }
        attempt.Token.ThrowIfCancellationRequested();
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
        _requests[id] = task;
        _ = task.ContinueWith(_ => _requests.TryRemove(id, out var ignored), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task ObserveAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (Volatile.Read(ref _disposed) == 0) LogException?.Invoke(exception); }
    }

    /// <summary>Creates a one-use pairing URL. Render it as a QR code in the host application.</summary>
    public string CreatePairingUrl()
    {
        if (Tunnel.Access is null || TunnelUrl is null) throw new InvalidOperationException("A paired tunnel must be connected first.");
        return TunnelUrl.TrimEnd('/') + "/_tunnelite/pair#code=" + Tunnel.Access.CreateChallenge().Code;
    }

    private async Task TunnelHttpConnectionAsync(HttpConnection httpConnection)
    {
        var publicUrl = Tunnel.PublicUrl;
        var requestUrl = $"{publicUrl}/tunnelite/request/{httpConnection.RequestId}";
        try
        {
            // Start the request to the public server
            using var fetch = ControlRequest(HttpMethod.Get, requestUrl);
            using var publicResponse = await ServerHttpClient.SendAsync(fetch, HttpCompletionOption.ResponseHeadersRead, _lifetime.Token);
            publicResponse.EnsureSuccessStatusCode();

            // Prepare the request to the local server
            using var localRequest = new HttpRequestMessage(new HttpMethod(httpConnection.Method), httpConnection.Path);

            // Copy headers from public response to local request
            foreach (var (key, value) in publicResponse.Headers)
            {
                if (key.StartsWith("X-TR-", StringComparison.OrdinalIgnoreCase))
                {
                    localRequest.Headers.TryAddWithoutValidation(key[5..], value);
                }
            }

            // Set the content of the local request to stream the data from the public response
            localRequest.Content = new StreamContent(await publicResponse.Content.ReadAsStreamAsync());
            if (httpConnection.ContentType != null)
            {
                localRequest.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(httpConnection.ContentType);
            }

            CancellationToken revoked = default;
            if (Tunnel.Access is { } access)
            {
                var path = httpConnection.Path[Tunnel.LocalUrl.TrimEnd('/').Length..].Split('?')[0];
                var decision = await access.CheckHttpAsync(localRequest, path, new Uri(TunnelUrl!), _lifetime.Token);
                if (decision.Response is { } denied)
                {
                    using (denied) await ForwardResponseAsync(requestUrl, denied, _lifetime.Token);
                    return;
                }
                revoked = decision.Revoked;
            }
            using var active = CancellationTokenSource.CreateLinkedTokenSource(revoked, _lifetime.Token);
            using var localResponse = await _localHttpClient.SendAsync(localRequest, HttpCompletionOption.ResponseHeadersRead, active.Token);
            await ForwardResponseAsync(requestUrl, localResponse, active.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogFailedRequest?.Invoke(httpConnection.Method, httpConnection.Path);
            LogException?.Invoke(ex);
            using var errorRequest = ControlRequest(HttpMethod.Delete, requestUrl);
            using var response = await ServerHttpClient.SendAsync(errorRequest, _lifetime.Token);
        }
    }

    private async Task ForwardResponseAsync(string requestUrl, HttpResponseMessage localResponse, CancellationToken cancellationToken)
    {
        // Prepare the request back to the public server
        using var publicRequest = ControlRequest(HttpMethod.Post, requestUrl);

        // Set the status code
        publicRequest.Headers.Add("X-T-Status", ((int)localResponse.StatusCode).ToString());

        // Copy headers from local response to public request
        foreach (var (key, value) in localResponse.Headers)
        {
            publicRequest.Headers.TryAddWithoutValidation($"X-TR-{key}", value);
        }

        // Copy content headers from local response to public request
        foreach (var (key, value) in localResponse.Content.Headers)
        {
            if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                publicRequest.Headers.TryAddWithoutValidation($"X-TC-{key}", value.First());
            }
            else
            {
                publicRequest.Headers.TryAddWithoutValidation($"X-TC-{key}", value);
            }
        }

        // Set the content of the public request to stream from the local response
        publicRequest.Content = new StreamingContent(await localResponse.Content.ReadAsStreamAsync());

        // Send the response back to the public server
        using var response = await ServerHttpClient.SendAsync(publicRequest, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task TunnelWsConnectionAsync(WsConnection wsConnection)
    {
        CancellationToken revoked = default;
        if (Tunnel.Access is { } access)
        {
            var cookies = wsConnection.Headers?.FirstOrDefault(h => h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)).Value;
            if (!access.Authorize(cookies is null ? null : string.Join("; ", cookies), new Uri(TunnelUrl!).Scheme == "https", out revoked))
            {
                await Connection.InvokeAsync("CloseWsAsync", wsConnection, _lifetime.Token);
                return;
            }
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(revoked, _lifetime.Token);

        try
        {
            using var webSocket = new ClientWebSocket();
            if (Tunnel.AllowUntrustedLocalCertificates == true) webSocket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

            // Ask the local app for the same subprotocols the public client asked for (e.g. Vite's "vite-hmr").
            foreach (var subProtocol in wsConnection.SubProtocols ?? [])
            {
                webSocket.Options.AddSubProtocol(subProtocol);
            }

            foreach (var (name, values) in wsConnection.Headers ?? [])
            {
                if (!IsWebSocketTransportHeader(name))
                {
                    var value = string.Join(name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ? "; " : ", ", values);
                    if (Tunnel.Access is not null && name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                        value = string.Join("; ", value.Split(';').Select(s => s.Trim()).Where(s =>
                            !s.StartsWith(ViewerTicket.CookieName(new Uri(TunnelUrl!).Scheme == "https") + "=", StringComparison.Ordinal)));
                    if (value.Length > 0) webSocket.Options.SetRequestHeader(name, value);
                }
            }

            await webSocket.ConnectAsync(new Uri(wsConnection.Path), cts.Token);

            var incomingTask = StreamIncomingWsAsync(webSocket, wsConnection, cts.Token);
            var outgoingTask = StreamOutgoingWsAsync(webSocket, wsConnection, cts.Token);

            await Task.WhenAny(incomingTask, outgoingTask);
        }
        catch (Exception ex)
        {
            LogFailedRequest?.Invoke("WS", wsConnection.Path);
            LogException?.Invoke(ex);
        }
        finally
        {
            await cts.CancelAsync();
            if (Tunnel.Access is not null && !_lifetime.IsCancellationRequested) await Connection.InvokeAsync("CloseWsAsync", wsConnection, _lifetime.Token);

            Log?.Invoke($"[WS] Connection {wsConnection.RequestId} closed.");
        }
    }

    private static bool IsWebSocketTransportHeader(string name) =>
        name.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase)
        || new[] { "Host", "Connection", "Upgrade", "Content-Length", "Transfer-Encoding", "Keep-Alive", "Proxy-Connection" }
            .Contains(name, StringComparer.OrdinalIgnoreCase);

    private async Task StreamIncomingWsAsync(WebSocket webSocket, WsConnection wsConnection, CancellationToken cancellationToken)
    {
        try
        {
            // StreamIncomingWsV2Async carries the EndOfMessage flag; the original StreamIncomingWsAsync is
            // still served for clients built before it existed.
            await foreach (var chunk in Connection.StreamAsync<WsChunk>("StreamIncomingWsV2Async", wsConnection, cancellationToken: cancellationToken))
            {
                if (chunk.Type == WebSocketMessageType.Close)
                {
                    if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    }
                }
                else if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.SendAsync(chunk.Data, chunk.Type, chunk.EndOfMessage, cancellationToken);
                }
                else
                {
                    break;
                }
            }
        }
        finally
        {
            Log?.Invoke($"[WS] Writing data to connection {wsConnection.RequestId} finished.");
        }
    }

    private Task StreamOutgoingWsAsync(WebSocket localWebSocket, WsConnection wsConnection, CancellationToken cancellationToken)
    {
        return Connection.InvokeAsync("StreamOutgoingWsAsync", StreamLocalWsAsync(localWebSocket, wsConnection, cancellationToken), wsConnection, cancellationToken: cancellationToken);
    }

    private async IAsyncEnumerable<WsChunk> StreamLocalWsAsync(WebSocket webSocket, WsConnection wsConnection, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TunnelProtocol.ChunkSize);

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                // Rent may hand back a bigger array; never read more than one chunk at a time.
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, TunnelProtocol.ChunkSize), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Forward the close so the public side gets a proper close handshake instead of a reset.
                    yield return new WsChunk([], WebSocketMessageType.Close);
                    break;
                }

                // Copy: the buffer is reused by the next ReceiveAsync before SignalR has serialized this item.
                yield return new WsChunk(buffer[..result.Count], result.MessageType, result.EndOfMessage);
            }
        }
        finally
        {
            Log?.Invoke($"[WS] Reading data from connection {wsConnection.RequestId} finished.");

            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task TunnelSseConnectionAsync(SseConnection sseConnection)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);

        try
        {
            // Send the request to the local server
            using var request = new HttpRequestMessage(new HttpMethod(sseConnection.Method), sseConnection.Path);

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            request.Content = new StringContent(sseConnection.Content);
            if (sseConnection.ContentType != null)
            {
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(sseConnection.ContentType);
            }

            using var response = await _localHttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);

            response.EnsureSuccessStatusCode();

            // Stream the SSE data from local server to the public tunnel
            var outgoingTask = StreamOutgoingSseAsync(response, sseConnection, cts.Token);

            // Wait for the streaming to complete
            await outgoingTask;
        }
        catch (Exception ex)
        {
            LogFailedRequest?.Invoke("SSE", sseConnection.Path);
            LogException?.Invoke(ex);
        }
        finally
        {
            await cts.CancelAsync();

            Log?.Invoke($"[SSE] Connection {sseConnection.RequestId} closed.");
        }
    }

    private async Task StreamOutgoingSseAsync(HttpResponseMessage response, SseConnection sseConnection, CancellationToken cancellationToken)
    {
        await Connection.InvokeAsync(
            "StreamOutgoingSseAsync",
            StreamLocalSseAsync(response, sseConnection, cancellationToken),
            sseConnection,
            cancellationToken: cancellationToken);
    }

    private async IAsyncEnumerable<byte[]> StreamLocalSseAsync(
        HttpResponseMessage response,
        SseConnection sseConnection,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TunnelProtocol.ChunkSize);

        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, 0, TunnelProtocol.ChunkSize, cancellationToken)) > 0)
            {
                yield return buffer[..bytesRead];
            }
        }
        finally
        {
            Log?.Invoke($"[SSE] Reading data from connection {sseConnection.RequestId} finished.");

            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<HttpTunnelResponse?> RegisterTunnelAsync(HttpTunnelRequest tunnel, CancellationToken cancellationToken)
    {
        tunnel.Subdomain = _currentTunnel?.Subdomain ?? tunnel.Subdomain;

        HttpTunnelResponse? tunnelResponse = null;

        while (tunnelResponse == null)
        {
            try
            {
                using var request = ControlRequest(HttpMethod.Post, $"{Tunnel.PublicUrl}/tunnelite/tunnel");
                request.Content = JsonContent.Create(tunnel, TunneliteJsonContext.Default.HttpTunnelRequest);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await ServerHttpClient.SendAsync(request, timeout.Token);
                if ((int)response.StatusCode is >= 400 and < 500)
                    throw new InvalidOperationException($"Tunnel registration was refused ({(int)response.StatusCode}).");
                response.EnsureSuccessStatusCode();

                tunnelResponse = await response.Content.ReadFromJsonAsync(TunneliteJsonContext.Default.HttpTunnelResponse);
                if (Tunnel.OwnerToken is not null && (tunnelResponse?.ProtectedEndpoint != true
                    || (Tunnel.RequirePairing && !tunnelResponse.RequirePairing)))
                    throw new InvalidOperationException("This relay must be upgraded to support protected endpoints and pairing.");
                tunnel.Subdomain = tunnelResponse?.Subdomain ?? tunnel.Subdomain;

                if (!response.IsSuccessStatusCode)
                {
                    LogError?.Invoke($"{tunnelResponse!.Message}:{tunnelResponse.Error}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                LogError?.Invoke($"[HTTP] An error occurred while registering the tunnel:");
                LogException?.Invoke(ex);

                await Task.Delay(5000, cancellationToken);
            }
        }

        return tunnelResponse;
    }

    private HttpRequestMessage ControlRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        if (Tunnel.OwnerToken is { } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
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
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return false;
                }

                LogError?.Invoke($"[HTTP] Cannot connect to the public server on {Tunnel.PublicUrl}.");

                await Task.Delay(5000, token);
            }
        }
    }
}
