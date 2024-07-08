using Microsoft.AspNetCore.SignalR.Client;
using System.CommandLine;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebSocketTunnel.Client;

public class Program
{
    private static readonly HttpClient HttpClient = new();
    private static HubConnection? Connection;
    private static readonly Guid InstanceId = Guid.NewGuid();
    private static readonly string Server = "https://localhost:7193";
    private static readonly int ChunkSize = 16 * 1024; // 16KB

    public static async Task Main(string[] args)
    {
        var localUrlArgument = new Argument<string>("localUrl", "The local URL to tunnel to.");

        var rootCommand = new RootCommand
        {
            localUrlArgument
        };

        rootCommand.Description = "CLI tool to create a tunnel to a local server.";

        rootCommand.SetHandler(async (string localUrl) =>
        {
            var registered = await RegisterTunnelAsync(Server, localUrl, InstanceId);

            if (registered)
            {
                await ConnectToServerAsync(Server, InstanceId);
            }

        }, localUrlArgument);

        await rootCommand.InvokeAsync(args);

        Console.ReadLine();
    }

    private static async Task<bool> RegisterTunnelAsync(string publicUrl, string localUrl, Guid instanceId)
    {
        var response = await HttpClient.PostAsJsonAsync(
            $"{publicUrl}/register-tunnel",
            new Tunnel
            {
                LocalUrl = localUrl,
                InstanceId = instanceId,
            });

        var message = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Tunnel created successfully: {message}");

            return true;
        }
        else
        {
            Console.WriteLine($"Failed to create tunnel. {message}");

            return false;
        }
    }

    private static async Task ConnectToServerAsync(string publicUrl, Guid instanceId)
    {
        Connection = new HubConnectionBuilder()
            .WithUrl($"{publicUrl}/wstunnel?instanceId={instanceId}")
            .WithAutomaticReconnect()
            .Build();

        Connection.On<RequestMetadata>("StartTunnelRequest", async (requestMetadata) =>
        {
            Console.WriteLine($"Received tunneling request: [{requestMetadata.Method}]{requestMetadata.Path}");

            await TunnelRequestAsync(requestMetadata);
        });

        Connection.Reconnected += connectionId =>
        {
            Console.WriteLine($"Reconnected. New ConnectionId {connectionId}");

            return Task.CompletedTask;
        };

        Connection.Closed += async (error) =>
        {
            Console.WriteLine("Connection closed... reconnecting");

            await Task.Delay(new Random().Next(0, 5) * 1000);

            await ConnectWithRetryAsync(Connection, CancellationToken.None);
        };

        await ConnectWithRetryAsync(Connection, CancellationToken.None);
    }

    private static async Task TunnelRequestAsync(RequestMetadata requestMetadata)
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

            var response = await HttpClient.SendAsync(requestMessage);

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
                Console.WriteLine($"Cannot connect to websocket server on {Server}");

                await Task.Delay(5000, token);
            }
        }
    }
}
