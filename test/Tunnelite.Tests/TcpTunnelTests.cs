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
    public async Task Stale_tcp_connection_cannot_destroy_replacement_connection_listener()
    {
        using var cts = new CancellationTokenSource(TestData.Timeout);
        var sharedClientId = Guid.NewGuid();
        await using var staleConnection = new HubConnectionBuilder()
            .WithUrl(host.PublicUrl + "/wsstcptunnel?clientId=" + sharedClientId)
            .AddMessagePackProtocol()
            .Build();
        await staleConnection.StartAsync(cts.Token);

        await using var replacementConnection = new HubConnectionBuilder()
            .WithUrl(host.PublicUrl + "/wsstcptunnel?clientId=" + sharedClientId)
            .AddMessagePackProtocol()
            .Build();
        await replacementConnection.StartAsync(cts.Token);

        var replacementResponse = await replacementConnection.InvokeAsync<TcpTunnelResponse>(
            "RegisterTunnelAsync",
            new TcpTunnelRequest
            {
                ClientId = sharedClientId,
                Host = "localhost",
                LocalPort = host.App.TcpPort,
                LocalUrl = $"tcp://localhost:{host.App.TcpPort}",
            },
            cts.Token);
        Assert.True(string.IsNullOrEmpty(replacementResponse.Error), replacementResponse.Error);
        Assert.True(replacementResponse.Port > 0);

        // Stale connection attempts to register after replacement connection is already active.
        var staleResponse = await staleConnection.InvokeAsync<TcpTunnelResponse>(
            "RegisterTunnelAsync",
            new TcpTunnelRequest
            {
                ClientId = sharedClientId,
                Host = "localhost",
                LocalPort = host.App.TcpPort,
                LocalUrl = $"tcp://localhost:{host.App.TcpPort}",
            },
            cts.Token);
        Assert.NotEmpty(staleResponse.Error);

        // The replacement connection's TCP listener must still accept connections.
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync("127.0.0.1", replacementResponse.Port, cts.Token);
        Assert.True(tcpClient.Connected);
    }

    [Fact]
    public async Task Slow_cleanup_for_one_tcp_client_does_not_block_another_client_registration()
    {
        var tunnelStore = new TcpTunnelStore();
        var clientStore = new TcpClientStore();
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await clientStore.SetActiveConnectionAsync(tunnelStore, clientA, "conn-a");
        var ctsA = new CancellationTokenSource();
        using var registration = ctsA.Token.Register(() =>
        {
            cleanupEntered.TrySetResult();
            releaseCleanup.Task.GetAwaiter().GetResult();
        });
        var listenerA = new TcpListenerContext { CancellationTokenSource = ctsA };
        Assert.True(await clientStore.TryBeginRegistrationAsync(tunnelStore, clientA, "conn-a"));
        Assert.True(await clientStore.TryCompleteRegistrationAsync(tunnelStore, clientA, "conn-a", listenerA));

        var disconnectA = Task.Run(() => clientStore.DisconnectAsync(tunnelStore, clientA, "conn-a"));
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // While client A is mid-cleanup, unrelated client B must register without waiting.
        await clientStore.SetActiveConnectionAsync(tunnelStore, clientB, "conn-b").WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(await clientStore.TryBeginRegistrationAsync(tunnelStore, clientB, "conn-b").WaitAsync(TimeSpan.FromSeconds(1)));
        await using var listenerB = new TcpListenerContext { CancellationTokenSource = new CancellationTokenSource() };
        Assert.True(await clientStore.TryCompleteRegistrationAsync(tunnelStore, clientB, "conn-b", listenerB).WaitAsync(TimeSpan.FromSeconds(1)));

        releaseCleanup.TrySetResult();
        await disconnectA.WaitAsync(TimeSpan.FromSeconds(2));
        await clientStore.DisconnectAsync(tunnelStore, clientB, "conn-b");
    }

    [Fact]
    public async Task Listener_cleanup_and_late_disconnect_never_retire_active_gate_or_close_replacement_listener()
    {
        var tunnelStore = new TcpTunnelStore();
        var clientStore = new TcpClientStore();
        var clientId = Guid.NewGuid();

        await clientStore.SetActiveConnectionAsync(tunnelStore, clientId, "conn-old");
        var oldListener = new TcpListenerContext
        {
            ConnectionId = "conn-old",
            CancellationTokenSource = new CancellationTokenSource(),
        };
        Assert.True(await clientStore.TryBeginRegistrationAsync(tunnelStore, clientId, "conn-old"));
        Assert.True(await clientStore.TryCompleteRegistrationAsync(tunnelStore, clientId, "conn-old", oldListener));

        // Fault/cleanup disposes the old listener while the old disconnect and replacement registration overlap.
        await clientStore.DisposeTcpListenerAsync(tunnelStore, clientId, oldListener);

        var oldDisconnect = clientStore.DisconnectAsync(tunnelStore, clientId, "conn-old");
        await clientStore.SetActiveConnectionAsync(tunnelStore, clientId, "conn-new");
        var newCts = new CancellationTokenSource();
        var newListener = new TcpListenerContext
        {
            ConnectionId = "conn-new",
            CancellationTokenSource = newCts,
        };
        Assert.True(await clientStore.TryBeginRegistrationAsync(tunnelStore, clientId, "conn-new"));
        Assert.True(await clientStore.TryCompleteRegistrationAsync(tunnelStore, clientId, "conn-new", newListener));
        await oldDisconnect;

        Assert.Equal("conn-new", tunnelStore.Connections[clientId]);
        Assert.False(newCts.IsCancellationRequested);

        await clientStore.DisconnectAsync(tunnelStore, clientId, "conn-new");
        Assert.True(newCts.IsCancellationRequested);
        Assert.Equal(0, clientStore.ActiveGateCount);
    }

    [Fact]
    public async Task Canceled_gate_acquisitions_and_queued_registrations_retire_gates_cleanly()
    {
        var tunnelStore = new TcpTunnelStore();
        var clientStore = new TcpClientStore();

        // Case 1: Pre-canceled acquisitions leave zero retained gates.
        using var canceledCts = new CancellationTokenSource();
        canceledCts.Cancel();
        for (var i = 0; i < 10_000; i++)
        {
            var id = Guid.NewGuid();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                clientStore.TryBeginRegistrationAsync(tunnelStore, id, "conn", canceledCts.Token));
        }
        Assert.Equal(0, clientStore.ActiveGateCount);

        // Case 2: Canceling a queued registration while disconnect cleanup holds the gate retires the gate once both finish.
        var clientId = Guid.NewGuid();
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await clientStore.SetActiveConnectionAsync(tunnelStore, clientId, "conn-1");
        var listenerCts = new CancellationTokenSource();
        using var registration = listenerCts.Token.Register(() =>
        {
            cleanupEntered.TrySetResult();
            releaseCleanup.Task.GetAwaiter().GetResult();
        });
        var listener = new TcpListenerContext { ConnectionId = "conn-1", CancellationTokenSource = listenerCts };
        Assert.True(await clientStore.TryBeginRegistrationAsync(tunnelStore, clientId, "conn-1"));
        Assert.True(await clientStore.TryCompleteRegistrationAsync(tunnelStore, clientId, "conn-1", listener));

        var disconnectTask = Task.Run(() => clientStore.DisconnectAsync(tunnelStore, clientId, "conn-1"));
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var queuedCts = new CancellationTokenSource();
        var queuedRegistration = clientStore.TryBeginRegistrationAsync(tunnelStore, clientId, "conn-1", queuedCts.Token);
        queuedCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedRegistration);

        releaseCleanup.TrySetResult();
        await disconnectTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, clientStore.ActiveGateCount);
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
