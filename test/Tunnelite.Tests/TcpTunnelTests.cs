using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Tunnelite.Server.TcpTunnel;
using Tunnelite.Tests.Infrastructure;

namespace Tunnelite.Tests;

/// <summary>
/// Raw TCP through a tunnel: public client -> public port on the server -> SDK -> local greet-and-echo service.
/// </summary>
public class TcpTunnelTests(TcpTunnelHost host) : IClassFixture<TcpTunnelHost>
{
    [Fact]
    public async Task Tcp_hub_rejects_missing_or_mismatched_client_ids()
    {
        await using var missingId = new HubConnectionBuilder()
            .WithUrl(host.PublicUrl + "/wsstcptunnel")
            .Build();
        await Assert.ThrowsAsync<HttpRequestException>(() => missingId.StartAsync());

        var connectedId = Guid.NewGuid();
        await using var validConnection = new HubConnectionBuilder()
            .WithUrl(host.PublicUrl + "/wsstcptunnel?clientId=" + connectedId)
            .AddMessagePackProtocol()
            .Build();
        await validConnection.StartAsync();

        var spoofedResponse = await validConnection.InvokeAsync<TcpTunnelResponse>("RegisterTunnelAsync", new TcpTunnelRequest
        {
            ClientId = Guid.NewGuid(),
            Host = "localhost",
            LocalPort = host.App.TcpPort,
            LocalUrl = $"tcp://localhost:{host.App.TcpPort}",
        });
        Assert.NotEmpty(spoofedResponse.Error);
    }

    [Fact]
    public async Task Multiple_tcp_tunnels_coexist_and_fallback_when_preferred_port_is_occupied()
    {
        using var cts = new CancellationTokenSource(TestData.Timeout);
        var secondClientId = Guid.NewGuid();
        await using var secondConnection = new HubConnectionBuilder()
            .WithUrl(host.PublicUrl + "/wsstcptunnel?clientId=" + secondClientId)
            .AddMessagePackProtocol()
            .Build();
        await secondConnection.StartAsync(cts.Token);

        // Request the port already occupied by the first TCP tunnel on the same single-node server.
        var fallbackResponse = await secondConnection.InvokeAsync<TcpTunnelResponse>(
            "RegisterTunnelAsync",
            new TcpTunnelRequest
            {
                ClientId = secondClientId,
                Host = "localhost",
                LocalPort = host.App.TcpPort,
                PublicPort = host.PublicTcpPort,
                LocalUrl = $"tcp://localhost:{host.App.TcpPort}",
            },
            cts.Token);

        Assert.True(string.IsNullOrEmpty(fallbackResponse.Error), fallbackResponse.Error);
        Assert.True(fallbackResponse.Port > 0);
        Assert.NotEqual(host.PublicTcpPort, fallbackResponse.Port);

        // The first tunnel remains unaffected on its original port.
        using var client = await ConnectAndReadGreetingAsync(cts.Token);
        await AssertEchoAsync(client, Encoding.UTF8.GetBytes("multi-tenant-tcp"), cts.Token);
    }
    [Fact]
    public async Task Data_sent_by_the_local_service_on_connect_reaches_the_public_client()
    {
        using var cts = new CancellationTokenSource(TestData.Timeout);
        using var client = await ConnectAndReadGreetingAsync(cts.Token);
    }

    [Fact]
    public async Task One_megabyte_round_trips_through_the_echo()
    {
        using var cts = new CancellationTokenSource(TestData.Timeout);
        using var client = await ConnectAndReadGreetingAsync(cts.Token);

        await AssertEchoAsync(client, TestData.Bytes(1_000_000), cts.Token);
    }

    [Fact]
    public async Task Several_connections_are_multiplexed_over_one_tunnel()
    {
        using var cts = new CancellationTokenSource(TestData.Timeout);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(async i =>
        {
            using var client = await ConnectAndReadGreetingAsync(cts.Token);
            await AssertEchoAsync(client, TestData.Bytes(200_000, seed: i), cts.Token);
        }));
    }

    private async Task<TcpClient> ConnectAndReadGreetingAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        await client.ConnectAsync("localhost", host.PublicTcpPort, cancellationToken);

        var greeting = new byte[LocalApp.TcpGreeting.Length];
        await TestData.ReadExactlyAsync(client.GetStream(), greeting, cancellationToken);

        Assert.Equal(LocalApp.TcpGreeting, Encoding.ASCII.GetString(greeting));

        return client;
    }

    private static async Task AssertEchoAsync(TcpClient client, byte[] payload, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();

        var receiving = Task.Run(async () =>
        {
            var echoed = new byte[payload.Length];
            await TestData.ReadExactlyAsync(stream, echoed, cancellationToken);
            return echoed;
        }, cancellationToken);

        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        Assert.Equal(payload, await receiving);
    }
}
