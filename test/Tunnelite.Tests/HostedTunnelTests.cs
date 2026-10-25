using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tunnelite.AspNetCore;
using Tunnelite.Tests.Infrastructure;

namespace Tunnelite.Tests;

public class HostedTunnelTests(BareTunnelHost host) : IClassFixture<BareTunnelHost>
{
    [Fact]
    public async Task Hosted_tunnel_waits_for_the_server_and_shuts_down_with_the_app()
    {
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = ["--urls", "http://127.0.0.1:0"] });
        builder.Logging.ClearProviders();
        builder.Services.AddTunnelite(options =>
        {
            options.PublicUrl = host.PublicUrl;
            options.OnReady = (client, _) => { ready.TrySetResult(client.TunnelUrl!); return Task.CompletedTask; };
        });
        await using var app = builder.Build();
        app.UseTunnelite(); // Existing startup code must not create a second tunnel when the hosted service is added.
        app.MapGet("/hosted", () => "hosted app");
        await app.StartAsync();
        var endpoint = await ready.Task.WaitAsync(TestData.Timeout);
        using var http = new HttpClient { Timeout = TestData.Timeout };
        Assert.Equal("hosted app", await http.GetStringAsync(endpoint + "/hosted"));
        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
