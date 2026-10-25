using Spectre.Console;
using Tunnelite.Client;

namespace Tunnelite.Tests;

public class CliActivityTests
{
    [Fact]
    public async Task Concurrent_requests_are_counted_without_retaining_a_request_log()
    {
        var activity = new CliActivity();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 100; index++) activity.Request("GET", $"http://localhost:1430/asset-{worker}-{index}.js?token=secret");
            activity.Failure("GET", "/missing");
            for (var index = 0; index < 10; index++) activity.Error("backend unavailable");
        })));
        var snapshot = activity.Snapshot();
        Assert.Equal(800, snapshot.Requests);
        Assert.Equal(8, snapshot.Failures);
        Assert.Equal(3, snapshot.Errors.Length);
        Assert.DoesNotContain("secret", snapshot.Latest);
        activity.Request("GET", "/good.js");
        Assert.Equal(3, activity.Snapshot().Errors.Length); // Successful assets must not hide failures.
        activity.Clear();
        Assert.Equal(0, activity.Snapshot().Requests);
        Assert.Empty(activity.Snapshot().Errors);
    }

    [Fact]
    public void Redirected_output_batches_a_burst_and_preserves_a_complete_url()
    {
        var output = new StringWriter();
        var display = new CliDisplay(AnsiConsole.Console, "http://localhost:1430", paired: true, live: false, output);
        var link = "https://example.tunnelite.com/_tunnelite/pair#code=" + new string('a', 43);
        display.Ready("https://example.tunnelite.com", link);
        for (var index = 0; index < 100; index++) display.Request("GET", $"/asset-{index}.js");
        display.RefreshPlain(force: true);
        display.RefreshPlain(force: true);
        var text = output.ToString();
        Assert.Contains("Pairing URL: " + link + Environment.NewLine, text);
        Assert.Contains("Requests: 100 | Proxy errors: 0 | Latest: GET /asset-99.js", text);
        Assert.Equal(1, text.Split(Environment.NewLine).Count(line => line.StartsWith("Requests:")));
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
    }
}
