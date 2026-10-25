using Tunnelite.Sdk;
using Tunnelite.Tests.Infrastructure;

namespace Tunnelite.Tests;

public class ClientLifecycleTests(BareTunnelHost host) : IClassFixture<BareTunnelHost>
{
    [Fact]
    public async Task Connected_means_the_endpoint_url_is_ready()
    {
        await using var client = new HttpTunnelClient(new HttpTunnelRequest
        {
            ClientId = Guid.NewGuid(), LocalUrl = host.App.Url, PublicUrl = host.PublicUrl,
        }, null);
        string? notifiedUrl = null;
        client.Connected += () => { notifiedUrl = client.TunnelUrl; return Task.CompletedTask; };
        await client.ConnectAsync().WaitAsync(TestData.Timeout);
        Assert.Equal(host.PublicUrl, notifiedUrl);
    }

    [Fact]
    public async Task Initial_connection_retry_can_be_cancelled_and_disposed()
    {
        await using var client = new HttpTunnelClient(new HttpTunnelRequest
        {
            ClientId = Guid.NewGuid(), LocalUrl = host.App.Url, PublicUrl = "http://127.0.0.1:1",
        }, null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConnectAsync(cancellation.Token));
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }
}
