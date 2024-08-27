using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;

namespace Tunnelite.Client.HttpTunnel;

public class HttpTunnelClient : ITunnelClient
{
    public event Func<Task>? Connected;
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
        ServerCertificateCustomValidationCallback = (message, cert, chain, sslPolicyErrors) => true,
    };
    private static readonly HttpClient ServerHttpClient = new();
    private static readonly HttpClient LocalHttpClient = new(LocalHttpClientHandler);

    private HttpTunnelResponse? _currentTunnel = null;
    private readonly HttpTunnelRequest Tunnel;

    public HttpTunnelClient(HttpTunnelRequest tunnel, LogLevel? logLevel)
    {
        Tunnel = tunnel;

        Connection = new HubConnectionBuilder()
            .WithUrl($"{tunnel.PublicUrl}/wsshttptunnel?clientId={tunnel.ClientId}")
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

        Connection.On<HttpConnection>("NewHttpConnection", (httpConnection) =>
        {
            Program.LogRequest(httpConnection.Method, httpConnection.Path);

            _ = TunnelHttpConnectionAsync(httpConnection);

            return Task.CompletedTask;
        });

        Connection.On<WsConnection>("NewWsConnection", (wsConnection) =>
        {
            Program.LogRequest("WS", wsConnection.Path);

            _ = TunnelWsConnectionAsync(wsConnection);

            return Task.CompletedTask;
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

    private async Task TunnelHttpConnectionAsync(HttpConnection httpConnection)
    {
        var publicUrl = Tunnel.PublicUrl;
        var requestUrl = $"{publicUrl}/tunnelite/request/{httpConnection.RequestId}";
        try
        {
            // Start the request to the public server
            using var publicResponse = await ServerHttpClient.GetAsync(requestUrl, HttpCompletionOption.ResponseHeadersRead);
            publicResponse.EnsureSuccessStatusCode();

            // Prepare the request to the local server
            using var localRequest = new HttpRequestMessage(new HttpMethod(httpConnection.Method), httpConnection.Path);

            // Copy headers from public response to local request
            foreach (var (key, value) in publicResponse.Headers)
            {
                if (key.StartsWith("X-TR-"))
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

            // Send the request to the local server and get the response
            using var localResponse = await LocalHttpClient.SendAsync(localRequest);

            // Prepare the request back to the public server
            using var publicRequest = new HttpRequestMessage(HttpMethod.Post, requestUrl);

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
            publicRequest.Content = new StreamContent(await localResponse.Content.ReadAsStreamAsync());

            // Send the response back to the public server
            using var response = await ServerHttpClient.SendAsync(publicRequest);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Program.LogError($"[HTTP] Unexpected error tunneling request: {ex.Message}.");
            using var errorRequest = new HttpRequestMessage(HttpMethod.Delete, requestUrl);
            using var response = await ServerHttpClient.SendAsync(errorRequest);
        }
    }

    private async Task TunnelWsConnectionAsync(WsConnection wsConnection)
    {
        using var cts = new CancellationTokenSource();

        try
        {
            using var webSocket = new ClientWebSocket();

            await webSocket.ConnectAsync(new Uri(wsConnection.Path), cts.Token);

            var incomingTask = StreamIncomingWsAsync(webSocket, wsConnection, cts.Token);
            var outgoingTask = StreamOutgoingWsAsync(webSocket, wsConnection, cts.Token);

            await Task.WhenAny(incomingTask, outgoingTask);
        }
        catch (Exception ex)
        {
            Program.LogError($"[WS] Failed to connect for connection {wsConnection.RequestId}: {ex.Message}.");
        }
        finally
        {
            cts.Cancel();

            Program.Log($"[WS] Connection {wsConnection.RequestId} closed.");
        }
    }

    private async Task StreamIncomingWsAsync(WebSocket webSocket, WsConnection wsConnection, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in Connection.StreamAsync<(ReadOnlyMemory<byte> Data, WebSocketMessageType Type)>("StreamIncomingWsAsync", wsConnection, cancellationToken: cancellationToken))
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    if (chunk.Type == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    }
                    else
                    {
                        await webSocket.SendAsync(chunk.Data, chunk.Type, true, cancellationToken);
                    }
                }
                else
                {
                    break;
                }
            }
        }
        finally
        {
            Program.Log($"[WS] Writing data to connection {wsConnection.RequestId} finished.");
        }
    }

    private async Task StreamOutgoingWsAsync(WebSocket localWebSocket, WsConnection wsConnection, CancellationToken cancellationToken)
    {
        await Connection.InvokeAsync("StreamOutgoingWsAsync", StreamLocalWsAsync(localWebSocket, wsConnection, cancellationToken), wsConnection, cancellationToken: cancellationToken);
    }

    private static async IAsyncEnumerable<(ReadOnlyMemory<byte>, WebSocketMessageType)> StreamLocalWsAsync(WebSocket webSocket, WsConnection wsConnection, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int chunkSize = 32 * 1024;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkSize);

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                yield return (new ReadOnlyMemory<byte>(buffer, 0, result.Count), result.MessageType);
            }
        }
        finally
        {
            Program.Log($"[WS] Reading data from connection {wsConnection.RequestId} finished.");

            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<HttpTunnelResponse?> RegisterTunnelAsync(HttpTunnelRequest tunnel)
    {
        tunnel.Subdomain = _currentTunnel?.Subdomain;

        HttpTunnelResponse? tunnelResponse = null;

        while (tunnelResponse == null)
        {
            try
            {
                var response = await ServerHttpClient.PostAsJsonAsync($"{Tunnel.PublicUrl}/tunnelite/tunnel", tunnel);

                tunnelResponse = await response.Content.ReadFromJsonAsync<HttpTunnelResponse?>();

                if (!response.IsSuccessStatusCode)
                {
                    Program.LogError($"{tunnelResponse!.Message}:{tunnelResponse.Error}");
                }
            }
            catch (Exception ex)
            {
                Program.LogError($"[HTTP] An error occurred while registering the tunnel {ex.Message}.");

                await Task.Delay(5000);
            }
        }

        return tunnelResponse;
    }

    private async Task<bool> ConnectWithRetryAsync(HubConnection connection, CancellationToken token)
    {
        while (true)
        {
            try
            {
                await connection.StartAsync(token);

                Connected?.Invoke();

                return true;
            }
            catch when (token.IsCancellationRequested)
            {
                return false;
            }
            catch
            {
                Program.LogError($"[HTTP] Cannot connect to the public server on {Tunnel.PublicUrl}.");

                await Task.Delay(5000, token);
            }
        }
    }
}