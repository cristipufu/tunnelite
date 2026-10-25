using System.Net;
using System.Net.WebSockets;
using System.Text;
using Tunnelite.Tests.Infrastructure;

namespace Tunnelite.Tests;

public class BrowserAuthenticationTests(TunnelHost host) : IClassFixture<TunnelHost>
{
    private HttpClient Browser() => new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
    {
        BaseAddress = new Uri(host.PublicUrl), Timeout = TestData.Timeout,
    };

    [Fact]
    public async Task Login_cookies_belong_to_the_browser_and_are_not_reused_by_the_proxy()
    {
        using var alice = Browser();
        using var login = await alice.GetAsync("/login");
        Assert.Contains("session=alice", login.Headers.GetValues("Set-Cookie").Single());
        using var bob = Browser();
        Assert.Equal("anonymous", await bob.GetStringAsync("/identity"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/identity");
        request.Headers.Add("Cookie", "session=alice");
        using var response = await alice.SendAsync(request);
        Assert.Equal("alice", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Redirects_are_returned_to_the_browser()
    {
        using var browser = Browser();
        using var response = await browser.GetAsync("/redirect");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/identity", response.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sse_preserves_credentials_resume_position_and_backend_rejection(bool authenticated)
    {
        using var browser = Browser();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/authenticated-events");
        request.Headers.Add("Accept", "text/event-stream");
        request.Headers.Add("Last-Event-ID", "42");
        if (authenticated)
        {
            request.Headers.Add("Cookie", "session=alice");
            request.Headers.Add("Authorization", "Bearer phone");
        }
        using var response = await browser.SendAsync(request);
        Assert.Equal(authenticated ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
        if (authenticated) Assert.Equal("data: 42\n\n", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WebSocket_backend_rejection_closes_the_public_connection()
    {
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TestData.Timeout);
        await socket.ConnectAsync(new Uri(host.PublicUrl.Replace("http://", "ws://") + "/authenticated-ws"), timeout.Token);
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[128]), timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, result.CloseStatus);
    }

    [Fact]
    public async Task WebSocket_preserves_browser_credentials_and_origin()
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Cookie", "session=alice");
        socket.Options.SetRequestHeader("Authorization", "Bearer phone");
        socket.Options.SetRequestHeader("Origin", "https://phone.example");
        using var timeout = new CancellationTokenSource(TestData.Timeout);
        await socket.ConnectAsync(new Uri(host.PublicUrl.Replace("http://", "ws://") + "/authenticated-ws"), timeout.Token);
        var (data, _) = await TestData.ReceiveMessageAsync(socket, timeout.Token);
        Assert.Equal("authenticated", Encoding.UTF8.GetString(data));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
    }
}
