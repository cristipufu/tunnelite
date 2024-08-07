using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebSocketTunnel.Client.HttpTunnel;

public class HttpTunnelClient
{
    private static readonly HttpClientHandler LocalHttpClientHandler = new()
    {
        ServerCertificateCustomValidationCallback = (message, cert, chain, sslPolicyErrors) => true,
    };
    private static readonly HttpClient ServerHttpClient = new();
    private static readonly HttpClient LocalHttpClient = new(LocalHttpClientHandler);

    private HttpTunnelResponse? _currentTunnel = null;
    private readonly HubConnection Connection;
    private readonly HttpTunnelRequest Tunnel;

    public HttpTunnelClient(HttpTunnelRequest tunnel, LogLevel logLevel)
    {
        Tunnel = tunnel;

        Connection = new HubConnectionBuilder()
            .WithUrl($"{tunnel.PublicUrl}/wsshttptunnel?clientId={tunnel.ClientId}")
            .AddMessagePackProtocol()
            .ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(logLevel);
                logging.AddConsole();
            })
            .WithAutomaticReconnect()
            .Build();

        Connection.On<HttpConnection>("NewHttpConnection", (httpConnection) =>
        {
            Console.WriteLine($"Received http tunneling request: [{httpConnection.Method}]{httpConnection.Path}");

            _ = TunnelConnectionAsync(httpConnection);

            return Task.CompletedTask;
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

    private async Task TunnelConnectionAsync(HttpConnection httpConnection)
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
                localRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(httpConnection.ContentType);
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
                publicRequest.Headers.TryAddWithoutValidation($"X-TC-{key}", value);
            }

            // Set the content of the public request to stream from the local response
            publicRequest.Content = new StreamContent(await localResponse.Content.ReadAsStreamAsync());

            // Send the response back to the public server
            using var response = await ServerHttpClient.SendAsync(publicRequest);

            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error tunneling request: {ex.Message}");

            using var errorRequest = new HttpRequestMessage(HttpMethod.Delete, requestUrl);
            using var response = await ServerHttpClient.SendAsync(errorRequest);
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

                if (response.IsSuccessStatusCode)
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
