using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tunnelite.Tests.Infrastructure;

namespace Tunnelite.Tests;

public sealed class SseLimitsHost : TunnelHost
{
    protected override void ConfigureServer(WebApplication server)
    {
        var limits = server.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits;
        limits.MaxRequestBodySize = 1024;
        limits.MinRequestBodyDataRate = new MinDataRate(100_000, TimeSpan.FromSeconds(2));
    }
}

public class SseLimitsTests(SseLimitsHost host) : IClassFixture<SseLimitsHost>
{
    [Fact]
    public async Task Idle_sse_is_not_treated_as_a_slow_upload()
    {
        using var timeout = new CancellationTokenSource(TestData.Timeout);
        using var http = new HttpClient { BaseAddress = new Uri(host.PublicUrl) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/gated-sse");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            Assert.Equal("data: first", await reader.ReadLineAsync(timeout.Token));
            Assert.Equal("", await reader.ReadLineAsync(timeout.Token));
            // Exceed this fixture's intentionally strict body-rate grace period.
            await Task.Delay(TimeSpan.FromSeconds(4), timeout.Token);
            host.App.ReleaseGatedSse();
            Assert.Equal("data: last", await reader.ReadLineAsync(timeout.Token));
        }
        finally { host.App.ReleaseGatedSse(); }
    }

    [Fact]
    public async Task Ordinary_transfers_still_enforce_the_upload_limit_and_finish_with_a_gateway_error()
    {
        using var timeout = new CancellationTokenSource(TestData.Timeout);
        using var http = new HttpClient { BaseAddress = new Uri(host.PublicUrl) };
        using var response = await http.GetAsync("/large?bytes=80000", timeout.Token);
        Assert.Equal(System.Net.HttpStatusCode.BadGateway, response.StatusCode);
        // Failure must clear the backend's original Content-Length and complete the public request.
        Assert.NotEmpty(await response.Content.ReadAsStringAsync(timeout.Token));
    }

    [Fact]
    public async Task Sse_can_exceed_the_ordinary_upload_size_limit()
    {
        using var timeout = new CancellationTokenSource(TestData.Timeout);
        using var http = new HttpClient { BaseAddress = new Uri(host.PublicUrl) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sse?count=2&size=40000&delay=0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        Assert.Contains("data: " + new string('a', 40000), body);
        Assert.Contains("data: " + new string('b', 40000), body);
    }
}
