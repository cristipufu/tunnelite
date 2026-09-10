using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using Tunnelite.Tests.Infrastructure;

namespace Tunnelite.Tests;

/// <summary>
/// HTTP, SSE and WebSocket traffic through an SDK tunnel, end to end: public client -> tunnel server -> SDK -> local app.
/// </summary>
public class HttpTunnelTests(TunnelHost host) : IClassFixture<TunnelHost>
{
    private readonly HttpClient _client = new() { BaseAddress = new Uri(host.PublicUrl), Timeout = TestData.Timeout };

    [Fact]
    public async Task Get_request_is_forwarded_to_the_local_app()
    {
        Assert.Equal("hello from local app", await _client.GetStringAsync("/hello"));
    }

    [Fact]
    public async Task Post_body_headers_and_status_round_trip()
    {
        var payload = TestData.Bytes(300_000);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/echo") { Content = new ByteArrayContent(payload) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("X-Echo", "round-trip");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("round-trip", response.Headers.GetValues("X-Echo").Single());
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(204)]
    [InlineData(404)]
    [InlineData(418)]
    [InlineData(503)]
    public async Task Status_codes_are_forwarded(int statusCode)
    {
        using var response = await _client.GetAsync($"/status/{statusCode}");

        Assert.Equal(statusCode, (int)response.StatusCode);
    }

    [Fact]
    public async Task Large_response_body_is_streamed_intact()
    {
        var body = await _client.GetByteArrayAsync("/large?bytes=2000000");

        Assert.Equal(TestData.Bytes(2_000_000), body);
    }

    [Fact]
    public async Task Concurrent_requests_are_all_served()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 25).Select(i => _client.GetStringAsync($"/hello?i={i}")));

        Assert.All(responses, response => Assert.Equal("hello from local app", response));
    }

    [Fact]
    public async Task Sse_events_arrive_as_they_are_produced_and_the_client_reports_no_errors()
    {
        var failuresBefore = host.ClientFailures.Count;

        var (events, arrivals) = await ReadSseEventsAsync("/sse?count=3&size=16&delay=300", expected: 3);

        Assert.Equal(["aaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb", "cccccccccccccccc"], events);
        Assert.True(arrivals[2] - arrivals[0] >= 400, $"Events were buffered instead of streamed (arrival times: {string.Join(", ", arrivals)} ms).");

        // Regression: the SDK used to raise a HubException every time an SSE stream finished.
        await AssertNoNewClientFailuresAsync(failuresBefore);
    }

    [Fact]
    public async Task Sse_events_larger_than_a_chunk_are_delivered_whole()
    {
        // 100 KB per event: several stream items per event and more than the 32 KB SignalR default limit.
        var (events, _) = await ReadSseEventsAsync("/sse?count=2&size=100000&delay=50", expected: 2);

        Assert.Equal([new string('a', 100_000), new string('b', 100_000)], events);
    }

    [Fact]
    public async Task WebSocket_text_message_is_echoed()
    {
        using var cts = new CancellationTokenSource(TestData.Timeout);
        using var socket = await ConnectWebSocketAsync("/ws", cts.Token);

        await socket.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, cts.Token);
        var (data, type) = await TestData.ReceiveMessageAsync(socket, cts.Token);

        Assert.Equal(WebSocketMessageType.Text, type);
        Assert.Equal("ping", Encoding.UTF8.GetString(data));
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(100_000)]
    [InlineData(600_000)]
    public async Task WebSocket_binary_message_is_echoed_as_one_message(int size)
    {
        using var cts = new CancellationTokenSource(TestData.Timeout);
        using var socket = await ConnectWebSocketAsync("/ws", cts.Token);
        var payload = TestData.Bytes(size);

        await socket.SendAsync(payload, WebSocketMessageType.Binary, true, cts.Token);
        var (data, type) = await TestData.ReceiveMessageAsync(socket, cts.Token);

        Assert.Equal(WebSocketMessageType.Binary, type);
        Assert.Equal(payload.Length, data.Length);
        Assert.Equal(payload, data);
    }

    [Fact]
    public async Task WebSocket_large_text_keeps_multibyte_characters_intact()
    {
        // 120 KB of UTF-8, two bytes per character: splitting it at an arbitrary byte would produce invalid text frames.
        var text = new string('é', 60_000);
        using var cts = new CancellationTokenSource(TestData.Timeout);
        using var socket = await ConnectWebSocketAsync("/ws", cts.Token);

        await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cts.Token);
        var (data, type) = await TestData.ReceiveMessageAsync(socket, cts.Token);

        Assert.Equal(WebSocketMessageType.Text, type);
        Assert.Equal(text, Encoding.UTF8.GetString(data));
    }

    [Fact]
    public async Task WebSocket_messages_keep_their_order()
    {
        const int count = 300;
        using var cts = new CancellationTokenSource(TestData.Timeout);
        using var socket = await ConnectWebSocketAsync("/ws", cts.Token);

        var sending = Task.Run(async () =>
        {
            for (var i = 0; i < count; i++)
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes($"message-{i}"), WebSocketMessageType.Text, true, cts.Token);
            }
        }, cts.Token);

        for (var i = 0; i < count; i++)
        {
            var (data, _) = await TestData.ReceiveMessageAsync(socket, cts.Token);
            Assert.Equal($"message-{i}", Encoding.UTF8.GetString(data));
        }

        await sending;
    }

    [Fact]
    public async Task WebSocket_messages_pushed_by_the_local_app_arrive_and_its_close_is_forwarded()
    {
        using var cts = new CancellationTokenSource(TestData.Timeout);
        using var socket = await ConnectWebSocketAsync("/ws-push?count=5", cts.Token);

        for (var i = 1; i <= 5; i++)
        {
            var (data, _) = await TestData.ReceiveMessageAsync(socket, cts.Token);
            Assert.Equal($"push-{i}", Encoding.UTF8.GetString(data));
        }

        var result = await socket.ReceiveAsync(new byte[16], cts.Token);

        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);

        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Theory]
    [InlineData(40_000)]
    [InlineData(500_000)]
    public async Task WebSocket_large_messages_pushed_by_the_local_app_arrive_whole(int size)
    {
        // The local app sends each message as one frame, so the SDK has to chunk it. Chunks used to be 32 KB,
        // which the server's SignalR limit rejected by closing the hub connection.
        using var cts = new CancellationTokenSource(TestData.Timeout);
        using var socket = await ConnectWebSocketAsync($"/ws-push?count=3&size={size}", cts.Token);

        for (var i = 1; i <= 3; i++)
        {
            var (data, type) = await TestData.ReceiveMessageAsync(socket, cts.Token);

            Assert.Equal(WebSocketMessageType.Binary, type);
            Assert.Equal(TestData.Bytes(size, seed: i), data);
        }

        var result = await socket.ReceiveAsync(new byte[16], cts.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
    }

    [Fact]
    public async Task WebSocket_subprotocol_is_negotiated_with_the_local_app()
    {
        // Vite's HMR client asks for "vite-hmr" and gives up unless the server confirms it; the local app only
        // accepts the connection when it is asked for. Both sides of the tunnel have to pass it through.
        using var cts = new CancellationTokenSource(TestData.Timeout);
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("vite-hmr");

        await socket.ConnectAsync(new Uri(host.PublicUrl.Replace("http://", "ws://") + "/ws"), cts.Token);

        Assert.Equal("vite-hmr", socket.SubProtocol);

        await socket.SendAsync(Encoding.UTF8.GetBytes("subprotocol?"), WebSocketMessageType.Text, true, cts.Token);
        var (data, _) = await TestData.ReceiveMessageAsync(socket, cts.Token);

        Assert.Equal("vite-hmr", Encoding.UTF8.GetString(data));
    }

    [Fact]
    public async Task WebSocket_without_a_subprotocol_stays_without_one()
    {
        using var cts = new CancellationTokenSource(TestData.Timeout);
        using var socket = await ConnectWebSocketAsync("/ws", cts.Token);

        Assert.Null(socket.SubProtocol);

        await socket.SendAsync(Encoding.UTF8.GetBytes("subprotocol?"), WebSocketMessageType.Text, true, cts.Token);
        var (data, _) = await TestData.ReceiveMessageAsync(socket, cts.Token);

        Assert.Equal("(none)", Encoding.UTF8.GetString(data));
    }

    [Fact]
    public async Task WebSocket_close_from_the_public_client_completes_the_handshake_without_errors()
    {
        var failuresBefore = host.ClientFailures.Count;
        using var cts = new CancellationTokenSource(TestData.Timeout);
        using var socket = await ConnectWebSocketAsync("/ws", cts.Token);

        await socket.SendAsync(Encoding.UTF8.GetBytes("bye"), WebSocketMessageType.Text, true, cts.Token);
        await TestData.ReceiveMessageAsync(socket, cts.Token);

        // Only completes once the server answers with its own close frame.
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);

        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);

        await AssertNoNewClientFailuresAsync(failuresBefore);
    }

    private async Task<ClientWebSocket> ConnectWebSocketAsync(string path, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(host.PublicUrl.Replace("http://", "ws://") + path), cancellationToken);
        return socket;
    }

    private async Task<(List<string> Events, List<long> ArrivalsMs)> ReadSseEventsAsync(string path, int expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var stopwatch = Stopwatch.StartNew();
        var events = new List<string>();
        var arrivals = new List<long>();

        while (events.Count < expected)
        {
            var line = await reader.ReadLineAsync().WaitAsync(TestData.Timeout);
            Assert.NotNull(line);

            if (line.StartsWith("data: "))
            {
                events.Add(line[6..]);
                arrivals.Add(stopwatch.ElapsedMilliseconds);
            }
        }

        // The tunneled response ends when the local app's response ends.
        await reader.ReadToEndAsync().WaitAsync(TestData.Timeout);

        return (events, arrivals);
    }

    private async Task AssertNoNewClientFailuresAsync(int countBefore)
    {
        // Give the SDK a moment to process the hub completion that follows the end of a stream.
        await Task.Delay(500);

        var failures = host.ClientFailures.Skip(countBefore).ToList();

        Assert.True(failures.Count == 0, "The SDK reported failures:\n" + string.Join("\n", failures));
    }
}
