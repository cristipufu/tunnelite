using System.Net;
using Tunnelite.Sdk;
using Tunnelite.Tests.Infrastructure;

namespace Tunnelite.Tests;

public class ErrorPageTests(BareTunnelHost host) : IClassFixture<BareTunnelHost>
{
    [Theory]
    [InlineData("/404.html", "HTTP 404", "Tunnel unavailable")]
    [InlineData("/500.html", "HTTP 502", "Can’t reach the local app")]
    [InlineData("/404.HTML", "HTTP 404", "Tunnel unavailable")]
    [InlineData("/500.HTML", "HTTP 502", "Can’t reach the local app")]
    public async Task Existing_root_domain_preview_urls_keep_their_status_and_cache_policy(
        string path, string badge, string title)
    {
        using var client = Browser();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = "tunnelite.com";
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.Public);
        Assert.Equal(TimeSpan.FromHours(1), response.Headers.CacheControl.MaxAge);
        await AssertPageAsync(response, badge, title);
    }

    [Theory]
    [InlineData("tunnelite.com")]
    [InlineData("offline.tunnelite.com")]
    public async Task Missing_pages_and_tunnels_return_a_self_contained_404(string requestHost)
    {
        using var client = Browser();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/untrusted-path?secret=never-reflect");
        request.Headers.Host = requestHost;
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertPageAsync(response, "HTTP 404", "Tunnel unavailable");
    }

    [Fact]
    public async Task Unreachable_local_app_returns_a_self_contained_502()
    {
        await using var tunnel = new HttpTunnelClient(new HttpTunnelRequest
        {
            LocalUrl = "http://127.0.0.1:1", PublicUrl = host.PublicUrl,
        }, null);
        await tunnel.ConnectAsync().WaitAsync(TestData.Timeout);
        using var client = Browser();
        using var response = await client.GetAsync("/untrusted-path?secret=never-reflect");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        await AssertPageAsync(response, "HTTP 502", "Can’t reach the local app");
    }

    private HttpClient Browser() => new() { BaseAddress = new Uri(host.PublicUrl), Timeout = TestData.Timeout };

    private static async Task AssertPageAsync(HttpResponseMessage response, string badge, string title)
    {
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(badge, html);
        Assert.Contains($"<h1 id=\"title\">{title}</h1>", html);
        Assert.Contains("class=\"logo\"", html);
        Assert.Contains("class=\"steps\"", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("untrusted-path", html);
        Assert.DoesNotContain("never-reflect", html);
        Assert.DoesNotContain("HTTP 500", html);
    }
}
