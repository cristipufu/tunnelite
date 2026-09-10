using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using Tunnelite.Tests.Infrastructure;

namespace Tunnelite.Tests;

/// <summary>
/// Runs only when <c>TUNNELITE_CLI</c> points at a built <c>tunnelite</c> binary (CI runs it against both the
/// regular build and the NativeAOT publish).
/// </summary>
public sealed class CliFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "TUNNELITE_CLI";

    public CliFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvironmentVariable)))
        {
            Skip = $"Set {EnvironmentVariable} to the path of a built tunnelite binary to run the CLI end-to-end tests.";
        }
    }
}

[Trait("Category", "Cli")]
public class CliTests(BareTunnelHost host) : IClassFixture<BareTunnelHost>
{
    [CliFact]
    public async Task The_cli_binary_reports_its_version()
    {
        var cli = Environment.GetEnvironmentVariable(CliFactAttribute.EnvironmentVariable)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var process = Process.Start(new ProcessStartInfo(cli, "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var stdout = (await process.StandardOutput.ReadToEndAsync(cts.Token)).Trim();
        var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);

        Assert.True(process.ExitCode == 0, $"--version exited with {process.ExitCode}: {stderr}");
        Assert.Matches(@"^\d+\.\d+\.\d+", stdout);
    }

    [CliFact]
    public async Task The_cli_binary_tunnels_http_sse_and_websockets()
    {
        var cli = Environment.GetEnvironmentVariable(CliFactAttribute.EnvironmentVariable)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var output = new ConcurrentQueue<string>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(cli, $"{host.App.Url} --publicUrl {host.PublicUrl}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            },
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            output.Enqueue(e.Data);
            if (e.Data.Contains("Public URL")) ready.TrySetResult();
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.Enqueue("stderr: " + e.Data); };

        Assert.True(process.Start(), $"Could not start {cli}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await ready.Task.WaitAsync(cts.Token);

            using var http = new HttpClient { BaseAddress = new Uri(host.PublicUrl) };

            // HTTP
            Assert.Equal("hello from local app", await http.GetStringAsync("/hello", cts.Token));

            // SSE
            using var request = new HttpRequestMessage(HttpMethod.Get, "/sse?count=2&size=8&delay=50");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var sse = await response.Content.ReadAsStringAsync(cts.Token);
            Assert.Contains("data: aaaaaaaa", sse);
            Assert.Contains("data: bbbbbbbb", sse);

            // WebSocket: text, then a message spanning several chunks
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(host.PublicUrl.Replace("http://", "ws://") + "/ws"), cts.Token);

            await socket.SendAsync(Encoding.UTF8.GetBytes("cli ping"), WebSocketMessageType.Text, true, cts.Token);
            var (text, _) = await TestData.ReceiveMessageAsync(socket, cts.Token);
            Assert.Equal("cli ping", Encoding.UTF8.GetString(text));

            var payload = TestData.Bytes(100_000);
            await socket.SendAsync(payload, WebSocketMessageType.Binary, true, cts.Token);
            var (binary, _) = await TestData.ReceiveMessageAsync(socket, cts.Token);
            Assert.Equal(payload, binary);

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);

            // Let the CLI process the stream completions, then make sure it printed no exception.
            await Task.Delay(1000, cts.Token);
            var log = string.Join("\n", output);
            Assert.DoesNotContain("Exception", log);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
