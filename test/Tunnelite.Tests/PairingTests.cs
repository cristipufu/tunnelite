using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using Tunnelite.Sdk;
using Tunnelite.Tests.Infrastructure;

namespace Tunnelite.Tests;

public sealed class PairingHost : IAsyncLifetime
{
    public BareTunnelHost Relay { get; } = new();
    public ManualClock Clock { get; } = new();
    public PairedAccess Access { get; }
    public PairingHost() : this(TunnelIdentity.Create(), null) { }
    internal PairingHost(TunnelIdentity identity, PairedAccessState? state) => Access = new(identity, state, clock: Clock);
    public HttpTunnelClient Tunnel { get; private set; } = null!;
    public async Task InitializeAsync()
    {
        await Relay.InitializeAsync();
        Tunnel = new(new HttpTunnelRequest { Access = Access, LocalUrl = Relay.App.Url, PublicUrl = Relay.PublicUrl }, null);
        await Tunnel.ConnectAsync().WaitAsync(TestData.Timeout);
    }
    public async Task DisposeAsync() { await Tunnel.DisposeAsync(); Access.Dispose(); await Relay.DisposeAsync(); }
}

public sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

public class PairingTests(PairingHost host) : IClassFixture<PairingHost>
{
    private HttpClient Browser(CookieContainer? cookies = null) => new(new HttpClientHandler
    {
        CookieContainer = cookies ?? new(), AllowAutoRedirect = false,
    }) { BaseAddress = new Uri(host.Relay.PublicUrl), Timeout = TestData.Timeout };

    private string Code() => host.Tunnel.CreatePairingUrl().Split("#code=")[1];

    [Fact]
    public async Task Anonymous_requests_and_websocket_upgrades_are_refused()
    {
        using var browser = Browser();
        using var response = await browser.GetAsync("/hello");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var events = new HttpRequestMessage(HttpMethod.Get, "/sse");
        events.Headers.Add("Accept", "text/event-stream");
        using var sse = await browser.SendAsync(events);
        Assert.Equal(HttpStatusCode.Unauthorized, sse.StatusCode);
        using var socket = new ClientWebSocket();
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(
            new Uri(host.Relay.PublicUrl.Replace("http://", "ws://") + "/ws"), CancellationToken.None));
    }

    [Fact]
    public async Task Pairing_is_single_use_and_issues_a_host_only_cookie()
    {
        using var browser = Browser();
        var code = Code();
        using var paired = await browser.PostAsJsonAsync("/_tunnelite/pair", new { code, name = "My phone" });
        Assert.Equal(HttpStatusCode.NoContent, paired.StatusCode);
        var cookie = paired.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("HttpOnly", cookie);
        Assert.Contains("SameSite=Strict", cookie);
        Assert.DoesNotContain("Domain=", cookie);
        Assert.DoesNotContain(host.Access.Identity.Secret, cookie);
        Assert.Equal("hello from local app", await browser.GetStringAsync("/hello"));
        using var stranger = Browser();
        using var used = await stranger.PostAsJsonAsync("/_tunnelite/pair", new { code });
        Assert.Equal(HttpStatusCode.Unauthorized, used.StatusCode);
    }

    [Fact]
    public async Task Concurrent_redemptions_enroll_exactly_one_device()
    {
        var code = Code();
        using var one = Browser();
        using var two = Browser();
        var responses = await Task.WhenAll(one.PostAsJsonAsync("/_tunnelite/pair", new { code }), two.PostAsJsonAsync("/_tunnelite/pair", new { code }));
        try { Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Unauthorized], responses.Select(r => r.StatusCode).OrderBy(s => (int)s)); }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Revoking_a_device_refuses_its_existing_cookie()
    {
        using var browser = Browser();
        using var paired = await browser.PostAsJsonAsync("/_tunnelite/pair", new { code = Code(), name = "Revoke me" });
        paired.EnsureSuccessStatusCode();
        var device = host.Access.State.Devices.Single(d => d.Name == "Revoke me");
        await host.Access.RevokeAsync(device.Id);
        using var response = await browser.GetAsync("/hello");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_modified_browser_ticket_is_refused()
    {
        using var browser = Browser();
        using var paired = await browser.PostAsJsonAsync("/_tunnelite/pair", new { code = Code() });
        paired.EnsureSuccessStatusCode();
        var cookie = paired.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        cookie = cookie[..^1] + (cookie[^1] == 'A' ? "B" : "A");
        using var stranger = Browser();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/hello");
        request.Headers.Add("Cookie", cookie);
        using var response = await stranger.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Paired_websockets_work_and_revocation_closes_them()
    {
        var jar = new CookieContainer();
        using var browser = Browser(jar);
        using var paired = await browser.PostAsJsonAsync("/_tunnelite/pair", new { code = Code(), name = "Streaming phone" });
        paired.EnsureSuccessStatusCode();
        using var socket = new ClientWebSocket();
        socket.Options.Cookies = jar;
        using var timeout = new CancellationTokenSource(TestData.Timeout);
        await socket.ConnectAsync(new Uri(host.Relay.PublicUrl.Replace("http://", "ws://") + "/ws"), timeout.Token);
        await socket.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Text, true, timeout.Token);
        var (bytes, _) = await TestData.ReceiveMessageAsync(socket, timeout.Token);
        Assert.Equal("hello", Encoding.UTF8.GetString(bytes));
        await host.Access.RevokeAsync(host.Access.State.Devices.Single(d => d.Name == "Streaming phone").Id);
        var close = await socket.ReceiveAsync(new byte[16], timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
    }

    [Fact]
    public async Task Pairing_bootstrap_clears_the_fragment_and_blocks_cross_origin_posts()
    {
        using var browser = Browser();
        using var page = await browser.GetAsync("/_tunnelite/pair");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("no-store", page.Headers.CacheControl!.ToString());
        var script = await browser.GetStringAsync("/_tunnelite/pair.js");
        Assert.True(script.IndexOf("history.replaceState", StringComparison.Ordinal) < script.IndexOf("fetch(", StringComparison.Ordinal));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_tunnelite/pair") { Content = JsonContent.Create(new { code = Code() }) };
        request.Headers.Add("Origin", "https://another-tunnel.tunnelite.com");
        using var refused = await browser.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task Expired_codes_cannot_enroll_a_device()
    {
        using var browser = Browser();
        var code = Code();
        var now = host.Clock.Now;
        host.Clock.Now = now.AddMinutes(6);
        try
        {
            using var response = await browser.PostAsJsonAsync("/_tunnelite/pair", new { code });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally { host.Clock.Now = now; }
    }

    [Fact]
    public async Task Desktop_state_restores_a_phone_after_both_processes_restart()
    {
        using var browser = Browser();
        using var paired = await browser.PostAsJsonAsync("/_tunnelite/pair", new { code = Code(), name = "Restored phone" });
        paired.EnsureSuccessStatusCode();
        var cookie = paired.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var restarted = new PairingHost(new TunnelIdentity(host.Access.Identity.Secret), host.Access.State);
        await restarted.InitializeAsync();
        try
        {
            using var restoredBrowser = new HttpClient { Timeout = TestData.Timeout };
            using var request = new HttpRequestMessage(HttpMethod.Get, restarted.Relay.PublicUrl + "/hello");
            request.Headers.Add("Cookie", cookie);
            using var response = await restoredBrowser.SendAsync(request);
            Assert.Equal("hello from local app", await response.Content.ReadAsStringAsync());
        }
        finally { await restarted.DisposeAsync(); }
    }

    [Fact]
    public async Task Revocation_ends_an_existing_sse_stream()
    {
        using var browser = Browser();
        using var paired = await browser.PostAsJsonAsync("/_tunnelite/pair", new { code = Code(), name = "Live phone" });
        paired.EnsureSuccessStatusCode();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/live-forever");
        request.Headers.Add("Accept", "text/event-stream");
        using var response = await browser.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal("data: live", await reader.ReadLineAsync().WaitAsync(TestData.Timeout));
        await host.Access.RevokeAsync(host.Access.State.Devices.Single(d => d.Name == "Live phone").Id);
        try { await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (IOException) { } // An aborted HTTP stream reports a reset instead of EOF.
    }
}
