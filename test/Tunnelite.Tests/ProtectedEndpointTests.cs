using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Tunnelite.Sdk;
using Tunnelite.Tests.Infrastructure;

namespace Tunnelite.Tests;

public class ProtectedEndpointTests(BareTunnelHost host) : IClassFixture<BareTunnelHost>
{
    [Fact]
    public async Task Identity_is_reconstructed_after_a_relay_restart_without_server_storage()
    {
        var identity = TunnelIdentity.Create();
        var restored = new TunnelIdentity(identity.Secret);
        Assert.Equal(identity.ClientId, restored.ClientId);
        Assert.Equal(identity.Subdomain, restored.Subdomain);
        for (var i = 0; i < 2; i++)
        {
            var relay = new BareTunnelHost();
            await relay.InitializeAsync();
            try
            {
                await using var tunnel = new HttpTunnelClient(new HttpTunnelRequest
                {
                    OwnerToken = restored.Secret, LocalUrl = relay.App.Url, PublicUrl = relay.PublicUrl,
                }, null);
                await tunnel.ConnectAsync().WaitAsync(TestData.Timeout);
                using var browser = new HttpClient { Timeout = TestData.Timeout };
                Assert.Equal("hello from local app", await browser.GetStringAsync(relay.PublicUrl + "/hello"));
            }
            finally { await relay.DisposeAsync(); }
        }
    }

    [Fact]
    public async Task A_protected_address_and_client_id_require_their_secret_even_on_a_fresh_server()
    {
        var identity = TunnelIdentity.Create();
        using var http = new HttpClient();
        using var registration = await http.PostAsJsonAsync(host.PublicUrl + "/tunnelite/tunnel", new
        {
            identity.ClientId, identity.Subdomain, localUrl = host.App.Url, protocolVersion = 2,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, registration.StatusCode);

        await using var attacker = new HubConnectionBuilder()
            .WithUrl(host.PublicUrl + "/wsshttptunnel?clientId=" + identity.ClientId).Build();
        await Assert.ThrowsAsync<HttpRequestException>(() => attacker.StartAsync());
    }

    [Fact]
    public async Task Knowing_the_address_does_not_authorize_a_different_secret()
    {
        var owner = TunnelIdentity.Create();
        var attacker = TunnelIdentity.Create();
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, host.PublicUrl + "/tunnelite/tunnel");
        request.Headers.Authorization = new("Bearer", attacker.Secret);
        request.Content = JsonContent.Create(new { owner.ClientId, owner.Subdomain, localUrl = host.App.Url, protocolVersion = 2 });
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Pending_request_bodies_and_responses_require_the_desktop_credential()
    {
        var owner = TunnelIdentity.Create();
        await using var control = new HubConnectionBuilder().WithUrl(
            host.PublicUrl + "/wsshttptunnel?clientId=" + owner.ClientId,
            options => options.AccessTokenProvider = () => Task.FromResult<string?>(owner.Secret)).Build();
        var notified = new TaskCompletionSource<HttpConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        control.On<HttpConnection>("NewHttpConnection", connection => notified.TrySetResult(connection));
        await control.StartAsync();
        using var http = new HttpClient { Timeout = TestData.Timeout };
        using var registration = new HttpRequestMessage(HttpMethod.Post, host.PublicUrl + "/tunnelite/tunnel");
        registration.Headers.Authorization = new("Bearer", owner.Secret);
        registration.Content = JsonContent.Create(new { owner.ClientId, owner.Subdomain, localUrl = host.App.Url, protocolVersion = 2 });
        using var registered = await http.SendAsync(registration);
        registered.EnsureSuccessStatusCode();
        var waiting = http.GetAsync(host.PublicUrl + "/hello");
        var pending = await notified.Task.WaitAsync(TestData.Timeout);
        var url = host.PublicUrl + "/tunnelite/request/" + pending.RequestId;
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, url);
            using var denied = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using var answer = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("owner response") };
        answer.Headers.Authorization = new("Bearer", owner.Secret);
        using var accepted = await http.SendAsync(answer);
        accepted.EnsureSuccessStatusCode();
        using var browserResponse = await waiting;
        Assert.Equal("owner response", await browserResponse.Content.ReadAsStringAsync());
    }
}
