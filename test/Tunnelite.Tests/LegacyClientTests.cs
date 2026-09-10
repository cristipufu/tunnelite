using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Tunnelite.Sdk;
using Tunnelite.Tests.Infrastructure;

namespace Tunnelite.Tests;

/// <summary>
/// Drives the server exactly the way clients built before the <c>EndOfMessage</c> flag do: the Microsoft
/// MessagePack hub protocol, the original <c>StreamIncomingWsAsync</c> method, <c>(data, type)</c> tuples in both
/// directions, and 32 KB reads from the local socket. Those clients are installed on people's machines and must keep
/// working against an updated server.
/// </summary>
public class LegacyClientTests(BareTunnelHost host) : IClassFixture<BareTunnelHost>
{
    /// <summary>What the pre-flag SDK used: <c>const int chunkSize = 32 * 1024</c>.</summary>
    private const int LegacyChunkSize = 32 * 1024;

    [Fact]
    public async Task A_client_using_the_original_wire_format_still_tunnels_websockets()
    {
        using var cts = new CancellationTokenSource(TestData.Timeout);
        await using var connection = await StartLegacyClientAsync(cts.Token);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(host.PublicUrl.Replace("http://", "ws://") + "/ws"), cts.Token);

        await socket.SendAsync(Encoding.UTF8.GetBytes("legacy ping"), WebSocketMessageType.Text, true, cts.Token);
        var (text, textType) = await TestData.ReceiveMessageAsync(socket, cts.Token);
        Assert.Equal(WebSocketMessageType.Text, textType);
        Assert.Equal("legacy ping", Encoding.UTF8.GetString(text));

        var payload = TestData.Bytes(10_000);
        await socket.SendAsync(payload, WebSocketMessageType.Binary, true, cts.Token);
        var (binary, binaryType) = await TestData.ReceiveMessageAsync(socket, cts.Token);
        Assert.Equal(WebSocketMessageType.Binary, binaryType);
        Assert.Equal(payload, binary);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Fact]
    public async Task A_client_using_the_original_32KB_chunks_can_forward_messages_larger_than_the_old_hub_limit()
    {
        // Installed clients cut local frames into 32 KB chunks, which lands just over SignalR's default 32 KB hub
        // message limit once the MessagePack envelope is added; the server used to answer by closing the whole hub
        // connection. The server now accepts larger hub messages, so those clients stop losing the tunnel. They still
        // deliver such a message as several messages, because the original format has no end-of-message flag.
        const int size = 100_000;
        using var cts = new CancellationTokenSource(TestData.Timeout);
        await using var connection = await StartLegacyClientAsync(cts.Token);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(host.PublicUrl.Replace("http://", "ws://") + $"/ws-push?count=1&size={size}"), cts.Token);

        using var received = new MemoryStream();

        while (received.Length < size)
        {
            var (data, type) = await TestData.ReceiveMessageAsync(socket, cts.Token);

            Assert.Equal(WebSocketMessageType.Binary, type);
            received.Write(data);
        }

        Assert.Equal(TestData.Bytes(size, seed: 1), received.ToArray());
    }

    private async Task<HubConnection> StartLegacyClientAsync(CancellationToken cancellationToken)
    {
        // The server routes the "localhost" host to the first registered tunnel, so wait until the previous
        // test's tunnel has been torn down before registering a new one.
        using var http = new HttpClient();

        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var probe = await http.GetAsync($"{host.PublicUrl}/hello", cancellationToken);

            if (probe.StatusCode == HttpStatusCode.NotFound)
            {
                break;
            }

            await Task.Delay(100, cancellationToken);
        }

        var clientId = Guid.NewGuid();

        var connection = new HubConnectionBuilder()
            .WithUrl($"{host.PublicUrl}/wsshttptunnel?clientId={clientId}")
            .AddMessagePackProtocol()
            .Build();

        connection.On<WsConnection>("NewWsConnection", wsConnection =>
        {
            _ = TunnelAsync(connection, wsConnection, cancellationToken);
            return Task.CompletedTask;
        });

        await connection.StartAsync(cancellationToken);

        using var registration = await http.PostAsJsonAsync($"{host.PublicUrl}/tunnelite/tunnel", new { clientId, localUrl = host.App.Url }, cancellationToken);
        registration.EnsureSuccessStatusCode();

        return connection;
    }

    // What the pre-EndOfMessage SDK did for each tunneled WebSocket, condensed.
    private static async Task TunnelAsync(HubConnection connection, WsConnection wsConnection, CancellationToken cancellationToken)
    {
        using var local = new ClientWebSocket();
        await local.ConnectAsync(new Uri(wsConnection.Path), cancellationToken);

        var incoming = Task.Run(async () =>
        {
            await foreach (var (data, type) in connection.StreamAsync<(ReadOnlyMemory<byte>, WebSocketMessageType)>("StreamIncomingWsAsync", wsConnection, cancellationToken))
            {
                if (type == WebSocketMessageType.Close)
                {
                    await local.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    break;
                }

                await local.SendAsync(data, type, true, cancellationToken);
            }
        }, cancellationToken);

        var outgoing = connection.InvokeAsync("StreamOutgoingWsAsync", ReadLocalAsync(local, cancellationToken), wsConnection, cancellationToken);

        await Task.WhenAny(incoming, outgoing);
    }

    private static async IAsyncEnumerable<(ReadOnlyMemory<byte>, WebSocketMessageType)> ReadLocalAsync(WebSocket socket, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[LegacyChunkSize];

        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            yield return (buffer[..result.Count], result.MessageType);
        }
    }
}
