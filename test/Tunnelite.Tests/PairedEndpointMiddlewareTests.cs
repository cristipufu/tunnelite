using Microsoft.AspNetCore.Http;
using Tunnelite.Server.HttpTunnel;

namespace Tunnelite.Tests;

public class PairedEndpointMiddlewareTests
{
    [Theory]
    [InlineData("GET", "text/html", false, true)]
    [InlineData("GET", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", false, true)]
    [InlineData("GET", "TEXT/HTML", false, true)]
    [InlineData("GET", "text/html;q=0,application/json", false, false)]
    [InlineData("GET", "not a valid accept value", false, false)]
    [InlineData("GET", "application/json", false, false)]
    [InlineData("GET", "text/event-stream,text/html", false, false)]
    [InlineData("GET", "text/html", true, false)]
    [InlineData("POST", "text/html", false, false)]
    [InlineData("HEAD", "text/html", false, false)]
    public async Task Unpaired_requests_are_rejected_without_contacting_the_desktop(
        string method, string accept, bool upgrade, bool html)
    {
        // No connected desktop exists: the relay must serve this response itself.
        var tunnels = new HttpTunnelStore();
        tunnels.Tunnels["private"] = new HttpTunnelRequest
        {
            ClientId = Guid.NewGuid(), RequirePairing = true, Subdomain = "private", LocalUrl = "http://127.0.0.1:1",
        };
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("private.tunnelite.com");
        context.Request.Path = "/untrusted-path";
        context.Request.Method = method;
        context.Request.Headers.Accept = accept;
        if (upgrade) context.Request.Headers.Upgrade = "websocket";
        using var body = new MemoryStream();
        context.Response.Body = body;
        var forwarded = false;
        var middleware = new PairedEndpointMiddleware(_ => { forwarded = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context, tunnels);

        Assert.False(forwarded);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal(html ? "text/html; charset=utf-8" : "text/plain; charset=utf-8", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("Accept", context.Response.Headers.Vary.ToString());
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions.ToString());
        Assert.Contains("frame-ancestors 'none'", context.Response.Headers.ContentSecurityPolicy.ToString());
        body.Position = 0;
        using var reader = new StreamReader(body);
        var text = await reader.ReadToEndAsync();
        if (html)
        {
            Assert.Contains("<main id=\"card\" data-state=\"required\">", text);
            Assert.Contains("This browser isn't paired", text);
            Assert.Contains("<kbd>p</kbd>", text);
            Assert.DoesNotContain("<script", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("untrusted-path", text);
        }
        else Assert.Equal("Scan the pairing QR code on your desktop to connect.", text);
    }
}
