using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebSocketTunnel.Client;

public class Program
{
    private static HubConnection? Connection;
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly string Server = "https://tunnelite.com";
    //private static readonly string Server = "https://localhost:7193";
    private static readonly int ChunkSize = 512 * 1024; // 512KB

    private static readonly HttpClientHandler LocalHttpClientHandler = new()
    {
        ServerCertificateCustomValidationCallback = (message, cert, chain, sslPolicyErrors) => true,
    };
    private static readonly HttpClient ServerHttpClient = new();
    private static readonly HttpClient LocalHttpClient = new(LocalHttpClientHandler);


    public static async Task Main(string[] args)
    {
        var localUrlArgument = new Argument<string>("localUrl", "The local URL to tunnel to.");
        var logLevelOption = new Option<LogLevel>(
            "--log",
            () => LogLevel.Warning,
            "The logging level (e.g., Trace, Debug, Information, Warning, Error, Critical)");

        var rootCommand = new RootCommand
        {
            localUrlArgument,
            logLevelOption
        };

        rootCommand.Description = "CLI tool to create a tunnel to a local server.";

        rootCommand.SetHandler(async (string localUrl, LogLevel logLevel) =>
        {
            await ConnectToServerAsync(localUrl, Server, ClientId, logLevel);

        }, localUrlArgument, logLevelOption);

        await rootCommand.InvokeAsync(args);

        Console.ReadLine();
    }

    private static async Task<TunnelResponse> RegisterTunnelAsync(string localUrl, string publicUrl, Guid clientId, TunnelResponse? existingTunnel)
    {
        var response = await ServerHttpClient.PostAsJsonAsync(
            $"{publicUrl}/tunnelite/tunnel",
            new Tunnel
            {
                LocalUrl = localUrl,
                ClientId = clientId,
                Subdomain = existingTunnel?.Subdomain,
            });

        var content = await response.Content.ReadFromJsonAsync<TunnelResponse>();

        if (response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Tunnel created successfully: {content!.TunnelUrl}");
        }
        else
        {
            Console.WriteLine($"{content!.Message}:{content.Error}");
        }

        return content;
    }

    private static async Task ConnectToServerAsync(string localUrl, string publicUrl, Guid clientId, LogLevel logLevel)
    {
        TunnelResponse? tunnel = null;

        Connection = new HubConnectionBuilder()
            .WithUrl($"{publicUrl}/wstunnel?clientId={clientId}", options =>
            {
                options.TransportMaxBufferSize = ChunkSize; 
                options.ApplicationMaxBufferSize = ChunkSize; 
            })
            .AddMessagePackProtocol()
            .ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(logLevel);
                logging.AddConsole();
            })
            .WithAutomaticReconnect()
            .Build();

        Connection.On<RequestMetadata>("StartTunnelRequest", async (requestMetadata) =>
        {
            Console.WriteLine($"Received tunneling request: [{requestMetadata.Method}]{requestMetadata.Path}");

            await TunnelRequestWithHttpAsync(publicUrl, requestMetadata);
            //await TunnelRequestWithWssAsync(requestMetadata);
        });

        Connection.Reconnected += async connectionId =>
        {
            Console.WriteLine($"Reconnected. New ConnectionId {connectionId}");

            tunnel = await RegisterTunnelAsync(localUrl, publicUrl, clientId, tunnel);
        };

        Connection.Closed += async (error) =>
        {
            Console.WriteLine("Connection closed... reconnecting");

            await Task.Delay(new Random().Next(0, 5) * 1000);

            if (await ConnectWithRetryAsync(Connection, CancellationToken.None))
            {
                tunnel = await RegisterTunnelAsync(localUrl, publicUrl, clientId, tunnel);
            }
        };

        if (await ConnectWithRetryAsync(Connection, CancellationToken.None))
        {
            tunnel = await RegisterTunnelAsync(localUrl, publicUrl, clientId, tunnel);
        }
    }

    private static async Task TunnelRequestWithHttpAsync(string publicUrl, RequestMetadata requestMetadata)
    {
        try
        {
            // Start the request to the public server
            using var publicResponse = await ServerHttpClient.GetAsync(
                $"{publicUrl}/tunnelite/request/{requestMetadata.RequestId}",
                HttpCompletionOption.ResponseHeadersRead);

            publicResponse.EnsureSuccessStatusCode();

            // Prepare the request to the local server
            var localRequest = new HttpRequestMessage(new HttpMethod(requestMetadata.Method), requestMetadata.Path);

            // Copy headers from public response to local request
            foreach (var header in publicResponse.Headers)
            {
                if (header.Key.StartsWith("X-TR-"))
                {
                    localRequest.Headers.TryAddWithoutValidation(header.Key[5..], header.Value);
                }
            }

            // Set the content of the local request to stream the data from the public response
            localRequest.Content = new StreamContent(await publicResponse.Content.ReadAsStreamAsync());

            if (requestMetadata.ContentType != null)
            {
                localRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(requestMetadata.ContentType);
            }

            // Send the request to the local server and get the response
            using var localResponse = await LocalHttpClient.SendAsync(localRequest);

            // Prepare the request back to the public server
            var publicRequest = new HttpRequestMessage(HttpMethod.Post, $"{publicUrl}/tunnelite/request/{requestMetadata.RequestId}");

            // Set the status code
            publicRequest.Headers.Add("X-T-Status", ((int)localResponse.StatusCode).ToString());

            // Copy headers from local response to public request
            foreach (var header in localResponse.Headers)
            {
                publicRequest.Headers.TryAddWithoutValidation($"X-TR-{header.Key}", header.Value);
            }

            // Copy content headers from local response to public request
            foreach (var header in localResponse.Content.Headers)
            {
                publicRequest.Headers.TryAddWithoutValidation($"X-TC-{header.Key}", header.Value);
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

            // todo replace wss
            await Connection!.SendAsync("CompleteWithErrorAsync", requestMetadata, ex.Message);
        }
    }

    private static async Task TunnelRequestWithWssAsync(RequestMetadata requestMetadata)
    {
        if (Connection == null)
        {
            return;
        }

        try
        {
            var requestBody = Connection.StreamAsync<byte[]>("StreamRequestBodyAsync", requestMetadata.RequestId);

            // Forward the request to the local server
            var requestMessage = new HttpRequestMessage(new HttpMethod(requestMetadata.Method), requestMetadata.Path)
            {
                Content = new HttpContentCallback(async (stream, token) =>
                {
                    await foreach (var chunk in requestBody.WithCancellation(token))
                    {
                        await stream.WriteAsync(chunk, token);
                    }
                })
            };

            foreach (var header in requestMetadata.Headers)
            {
                requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (requestMetadata.ContentType != null)
            {
                requestMessage.Content.Headers.ContentType = new MediaTypeHeaderValue(requestMetadata.ContentType);
            }

            var response = await LocalHttpClient.SendAsync(requestMessage);

            var responseMetadata = new ResponseMetadata
            {
                RequestId = requestMetadata.RequestId,
                StatusCode = response.StatusCode,
                Headers = response.Headers.ToDictionary(x => x.Key, x => string.Join(", ", x.Value)),
                ContentHeaders = response.Content.Headers.ToDictionary(x => x.Key, x => string.Join(", ", x.Value)),
            };

            // Stream the response back to the server
            await Connection.InvokeAsync("StreamResponseBodyAsync", responseMetadata, StreamResponseBodyAsync(await response.Content.ReadAsStreamAsync()));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error tunneling request: {ex.Message}");

            await Connection.SendAsync("CompleteWithErrorAsync", requestMetadata, ex.Message);
        }
    }

    private static async IAsyncEnumerable<byte[]> StreamResponseBodyAsync(Stream response)
    {
        var buffer = new byte[ChunkSize];
        int bytesRead;

        while ((bytesRead = await response.ReadAsync(buffer)) > 0)
        {
            if (bytesRead == buffer.Length)
            {
                yield return buffer;
            }
            else
            {
                var chunk = new byte[bytesRead];

                Array.Copy(buffer, chunk, bytesRead);

                yield return chunk;
            }
        }
    }

    private static async Task<bool> ConnectWithRetryAsync(HubConnection connection, CancellationToken token)
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
                Console.WriteLine($"Cannot connect to WebSocket server on {Server}");

                await Task.Delay(5000, token);
            }
        }
    }
}
