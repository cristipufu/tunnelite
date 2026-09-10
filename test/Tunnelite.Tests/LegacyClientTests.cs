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
/// MessagePack hub protocol, the original <c>StreamIncomingWsAsync</c> method, and <c>(data, type)</c> tuples in
/// both directions. Those clients are installed on people's machines and must keep working against an updated server.
/// </summary>
public class LegacyClientTests(BareTunnelHost host) : IClassFixture<BareTunnelHost>
{
    [Fact]
    public async Task A_client_using_the_original_wire_format_still_tunnels_websockets()
    {
        using var cts = new CancellationTokenSource(TestData.Timeout);
        var clientId = Guid.NewGuid();

        await using var connection = new HubConnectionBuilder()
            .WithUrl($"{host.PublicUrl}/wsshttptunnel?clientId={clientId}")
            .AddMessagePackProtocol()
            .Build();

        connection.On<WsConnection>("NewWsConnection", wsConnection =>
        {
            _ = TunnelAsync(connection, wsConnection, cts.Token);
            return Task.CompletedTask;
        });

        await connection.StartAsync(cts.Token);

        using var http = new HttpClient();
        using var registration = await http.PostAsJsonAsync($"{host.PublicUrl}/tunnelite/tunnel", new { clientId, localUrl = host.App.Url }, cts.Token);
        registration.EnsureSuccessStatusCode();

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
        var buffer = new byte[16 * 1024];

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
