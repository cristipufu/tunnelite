using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Tunnelite.Sdk;
using Tunnelite.Tests.Infrastructure;

namespace Tunnelite.Tests;

/// <summary>
/// Drives the server the way a client that normalises header names does - every browser's fetch, and
/// every HTTP/2 client, since HTTP/2 mandates lowercase field names. The SDK sends canonical casing
/// over HTTP/1.1, so nothing else here would notice the server matching the prefixes case-sensitively.
/// </summary>
public class LowercaseHeaderClientTests(BareTunnelHost host) : IClassFixture<BareTunnelHost>
{
    [Fact]
    public async Task Response_status_content_type_and_headers_survive_lowercase_prefixes()
    {
        using var cts = new CancellationTokenSource(TestData.Timeout);
        await using var connection = await StartLowercaseClientAsync(cts.Token);

        using var response = await new HttpClient { Timeout = TestData.Timeout }
            .GetAsync($"{host.PublicUrl}/anything", cts.Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("round-trip", response.Headers.GetValues("X-Echo").Single());
        Assert.Equal("served by a lowercase client", await response.Content.ReadAsStringAsync(cts.Token));
    }

    [Fact]
    public async Task Hop_by_hop_headers_are_still_filtered_when_the_prefix_is_lowercase()
    {
        using var cts = new CancellationTokenSource(TestData.Timeout);
        await using var connection = await StartLowercaseClientAsync(cts.Token);

        using var response = await new HttpClient { Timeout = TestData.Timeout }
            .GetAsync($"{host.PublicUrl}/anything", cts.Token);

        // The client sends "x-tr-upgrade"; matching the prefix case-insensitively without doing the same
        // for the deny list would let a hop-by-hop header through onto the response.
        Assert.False(response.Headers.Contains("Upgrade"));
    }

    private async Task<HubConnection> StartLowercaseClientAsync(CancellationToken cancellationToken)
    {
        // The server routes the "localhost" host to the first registered tunnel, so wait until the
        // previous test's tunnel has been torn down before registering a new one.
        using var http = new HttpClient();

        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var probe = await http.GetAsync($"{host.PublicUrl}/hello", cancellationToken);

            if (probe.StatusCode == HttpStatusCode.NotFound)
            {
                break;
            }

            await Task.Delay(100, cancellationToken);
        }

        var clientId = Guid.NewGuid();
        var connection = new HubConnectionBuilder()
            .WithUrl($"{host.PublicUrl}/wsshttptunnel?clientId={clientId}")
            .Build();

        connection.On<HttpConnection>("NewHttpConnection", httpConnection =>
        {
            _ = RespondAsync(httpConnection, cancellationToken);
            return Task.CompletedTask;
        });

        await connection.StartAsync(cancellationToken);

        using var registration = await http.PostAsJsonAsync(
            $"{host.PublicUrl}/tunnelite/tunnel",
            new { clientId, localUrl = host.App.Url },
            cancellationToken);
        registration.EnsureSuccessStatusCode();

        return connection;
    }

    /// <summary>Answers without touching the local app: the point is the shape of the response headers.</summary>
    private async Task RespondAsync(HttpConnection httpConnection, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var requestUrl = $"{host.PublicUrl}/tunnelite/request/{httpConnection.RequestId}";

        using var incoming = await http.GetAsync(requestUrl, cancellationToken);
        incoming.EnsureSuccessStatusCode();

        using var outgoing = new HttpRequestMessage(HttpMethod.Post, requestUrl)
        {
            Content = new StringContent("served by a lowercase client"),
        };

        outgoing.Headers.TryAddWithoutValidation("x-t-status", "201");
        outgoing.Headers.TryAddWithoutValidation("x-tc-content-type", "text/plain; charset=utf-8");
        outgoing.Headers.TryAddWithoutValidation("x-tr-x-echo", "round-trip");
        outgoing.Headers.TryAddWithoutValidation("x-tr-upgrade", "websocket");

        using var ack = await http.SendAsync(outgoing, cancellationToken);
        ack.EnsureSuccessStatusCode();
    }
}
