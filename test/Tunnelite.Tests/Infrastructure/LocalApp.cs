using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Tunnelite.Tests.Infrastructure;

/// <summary>
/// The "application running on the developer's machine": an HTTP API with SSE and WebSocket endpoints, and a TCP
/// service that greets and then echoes. Everything binds to an ephemeral loopback port.
/// </summary>
public sealed class LocalApp : IAsyncDisposable
{
    public const string TcpGreeting = "HELLO\n";

    private readonly WebApplication _app;
    private readonly TcpListener _tcp;
    private readonly CancellationTokenSource _cts = new();

    public string Url { get; }
    public int TcpPort { get; }

    private LocalApp(WebApplication app, TcpListener tcp)
    {
        _app = app;
        _tcp = tcp;

        Url = $"http://localhost:{new Uri(app.Urls.First()).Port}";
        TcpPort = ((IPEndPoint)tcp.LocalEndpoint).Port;

        _ = AcceptTcpAsync(_cts.Token);
    }

    public static async Task<LocalApp> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = ["--urls", "http://127.0.0.1:0"],
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var app = builder.Build();
        app.UseWebSockets();

        app.MapGet("/hello", () => "hello from local app");

        app.MapGet("/status/{code:int}", (int code) => Results.StatusCode(code));

        app.MapGet("/large", (int bytes) => Results.Bytes(TestData.Bytes(bytes), "application/octet-stream"));

        app.MapPost("/echo", async (HttpContext context) =>
        {
            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.ContentType = context.Request.ContentType ?? "application/octet-stream";

            if (context.Request.Headers.TryGetValue("X-Echo", out var echo))
            {
                context.Response.Headers["X-Echo"] = echo;
            }

            await context.Request.Body.CopyToAsync(context.Response.Body);
        });

        // Server-sent events: `count` events of `size` characters, `delay` ms apart.
        app.MapGet("/sse", async (HttpContext context, int count = 3, int size = 16, int delay = 100) =>
        {
            context.Response.ContentType = "text/event-stream";

            for (var i = 0; i < count; i++)
            {
                await context.Response.WriteAsync($"id: {i}\ndata: {new string((char)('a' + i % 26), size)}\n\n");
                await context.Response.Body.FlushAsync();
                await Task.Delay(delay);
            }
        });

        // Echo every frame back exactly as received: same type, same end-of-message flag.
        app.Map("/ws", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[64 * 1024];

            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "echo done", CancellationToken.None);
                    break;
                }

                await socket.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, context.RequestAborted);
            }
        });

        // Pushes `count` messages without waiting for the client, then closes. With `size` > 0 each message is a
        // single binary frame of that many bytes (seeded by its index), the way a real app hands large payloads to
        // the SDK.
        app.Map("/ws-push", async (HttpContext context, int count = 5, int size = 0) =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();

            for (var i = 1; i <= count; i++)
            {
                if (size > 0)
                {
                    await socket.SendAsync(TestData.Bytes(size, seed: i), WebSocketMessageType.Binary, true, context.RequestAborted);
                }
                else
                {
                    await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes($"push-{i}"), WebSocketMessageType.Text, true, context.RequestAborted);
                }
            }

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        });

        await app.StartAsync();

        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();

        return new LocalApp(app, tcp);
    }

    private async Task AcceptTcpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _tcp.AcceptTcpClientAsync(cancellationToken);
                _ = GreetAndEchoAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task GreetAndEchoAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(TcpGreeting), cancellationToken);
                await stream.CopyToAsync(stream, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _tcp.Stop();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
