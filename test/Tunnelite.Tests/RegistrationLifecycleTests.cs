using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Tunnelite.Sdk;
using Tunnelite.Tests.Infrastructure;
using HttpTunnelStore = Tunnelite.Server.HttpTunnel.HttpTunnelStore;

namespace Tunnelite.Tests;

public class RegistrationLifecycleTests(BareTunnelHost host) : IClassFixture<BareTunnelHost>
{
    [Fact]
    public async Task A_registration_without_a_connection_does_not_retain_server_state()
    {
        using var http = new HttpClient();
        var id = Guid.NewGuid();
        using var response = await http.PostAsJsonAsync(host.PublicUrl + "/tunnelite/tunnel", new
        {
            clientId = id, localUrl = host.App.Url,
        });
        Assert.Equal(425, (int)response.StatusCode);
        Assert.DoesNotContain(host.ServerServices.GetRequiredService<HttpTunnelStore>().Tunnels.Values, t => t.ClientId == id);
    }

    [Fact]
    public async Task A_late_disconnect_cannot_remove_a_replacement_or_its_pending_request()
    {
        var identity = TunnelIdentity.Create();
        HubConnection Connection() => new HubConnectionBuilder().WithUrl(host.PublicUrl + "/wsshttptunnel?clientId=" + identity.ClientId,
            options => options.AccessTokenProvider = () => Task.FromResult<string?>(identity.Secret)).Build();
        using var http = new HttpClient { Timeout = TestData.Timeout };
        async Task RegisterAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, host.PublicUrl + "/tunnelite/tunnel");
            request.Headers.Authorization = new("Bearer", identity.Secret);
            request.Content = JsonContent.Create(new { identity.ClientId, identity.Subdomain, localUrl = host.App.Url, protocolVersion = 2 });
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        await using var previous = Connection();
        await previous.StartAsync();
        await RegisterAsync();
        await using var replacement = Connection();
        var notified = new TaskCompletionSource<HttpConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        replacement.On<HttpConnection>("NewHttpConnection", request => notified.TrySetResult(request));
        await replacement.StartAsync();
        await RegisterAsync();
        var browser = http.GetAsync(host.PublicUrl + "/hello");
        var pending = await notified.Task.WaitAsync(TestData.Timeout);
        await previous.DisposeAsync();
        using var answer = new HttpRequestMessage(HttpMethod.Post, host.PublicUrl + "/tunnelite/request/" + pending.RequestId)
        {
            Content = new StringContent("replacement desktop"),
        };
        answer.Headers.Authorization = new("Bearer", identity.Secret);
        using var accepted = await http.SendAsync(answer);
        accepted.EnsureSuccessStatusCode();
        using var result = await browser;
        Assert.Equal("replacement desktop", await result.Content.ReadAsStringAsync());
    }
}
