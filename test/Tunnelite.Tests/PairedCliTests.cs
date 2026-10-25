using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Tunnelite.Tests.Infrastructure;

namespace Tunnelite.Tests;

[Trait("Category", "Cli")]
public class PairedCliTests(BareTunnelHost host) : IClassFixture<BareTunnelHost>
{
    [CliFact]
    public async Task Serve_pairs_a_phone_restores_it_after_restart_and_revokes_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tunnelite-cli-pair-" + Guid.NewGuid());
        var path = Path.Combine(directory, "desktop.json");
        try
        {
            using var browser = new HttpClient { BaseAddress = new Uri(host.PublicUrl), Timeout = TestData.Timeout };
            string secret;
            await using (var first = await RunningCli.StartAsync(host, path))
            {
                using var anonymous = await browser.GetAsync("/hello");
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
                using var paired = await browser.PostAsJsonAsync("/_tunnelite/pair", new { code = first.PairingUrl.Split("#code=")[1], name = "CLI phone" });
                Assert.Equal(HttpStatusCode.NoContent, paired.StatusCode);
                Assert.Equal("hello from local app", await browser.GetStringAsync("/hello"));
                using var state = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                secret = state.RootElement.GetProperty("secret").GetString()!;
                Assert.DoesNotContain(secret, first.PairingUrl);
                if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                await first.QuitAsync();
            }
            await using var restarted = await RunningCli.StartAsync(host, path);
            Assert.Equal("hello from local app", await browser.GetStringAsync("/hello"));
            using var restored = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(secret, restored.RootElement.GetProperty("secret").GetString());
            var id = restored.RootElement.GetProperty("access").GetProperty("devices")[0].GetProperty("id").GetGuid();
            await restarted.CommandAsync("revoke " + id);
            await restarted.WaitForAsync("Device access revoked.");
            using var revoked = await browser.GetAsync("/hello");
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            await restarted.QuitAsync();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class RunningCli : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ConcurrentQueue<string> _output = new();
        public string PairingUrl { get; private set; } = "";
        private RunningCli(Process process) => _process = process;
        public static async Task<RunningCli> StartAsync(BareTunnelHost host, string path)
        {
            var info = new ProcessStartInfo(Environment.GetEnvironmentVariable(CliFactAttribute.EnvironmentVariable)!)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (var arg in new[] { "serve", host.App.Url, "--publicUrl", host.PublicUrl, "--identity-file", path }) info.ArgumentList.Add(arg);
            var process = new Process { StartInfo = info };
            var running = new RunningCli(process);
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) running._output.Enqueue(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) running._output.Enqueue(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                var line = await running.WaitForAsync("Pairing URL: ");
                running.PairingUrl = line["Pairing URL: ".Length..];
                return running;
            }
            catch { await running.DisposeAsync(); throw; }
        }
        public async Task<string> WaitForAsync(string prefix)
        {
            using var timeout = new CancellationTokenSource(TestData.Timeout);
            while (!timeout.IsCancellationRequested)
            {
                var line = _output.FirstOrDefault(s => s.StartsWith(prefix, StringComparison.Ordinal));
                if (line is not null) return line;
                if (_process.HasExited) throw new InvalidOperationException("CLI exited before " + prefix + ": " + string.Join("\n", _output));
                await Task.Delay(50, timeout.Token);
            }
            throw new TimeoutException("CLI did not print " + prefix);
        }
        public async Task CommandAsync(string command)
        {
            await _process.StandardInput.WriteLineAsync(command);
            await _process.StandardInput.FlushAsync();
        }
        public async Task QuitAsync()
        {
            await CommandAsync("q");
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, _process.ExitCode);
        }
        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
            _process.Dispose();
        }
    }
}
