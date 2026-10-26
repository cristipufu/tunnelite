using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Acplite.Sdk;
using Acplite.Sdk.Protocol;
using Acplite.Sdk.Runtime;
using Tunnelite.Acp;
using Tunnelite.Sdk;
using Tunnelite.Tests.Infrastructure;

namespace Tunnelite.Tests;

public class AcpGatewayTests(BareTunnelHost host) : IClassFixture<BareTunnelHost>
{
    [Fact]
    public async Task AcpGatewayServer_serves_mobile_ui_and_orchestrates_sessions_queueing_steering_and_permissions_over_tunnel()
    {
        var fakeClaude = new ControllableTestAdapter(AgentProviderKind.Claude, "claude", "Claude Code");
        var fakeAgy = new ControllableTestAdapter(AgentProviderKind.Antigravity, "agy", "Google Antigravity");
        await using var runtime = new AcpliteRuntime([fakeClaude, fakeAgy]);
        await using var orchestrator = new AcpGatewayOrchestrator(scopedProvider: null, Path.GetTempPath(), runtime);
        await using var acpServer = await AcpGatewayServer.StartNewAsync(orchestrator, ownsOrchestrator: false);

        using var cts = new CancellationTokenSource(TestData.Timeout);
        var identity = TunnelIdentity.Create();
        using var access = new PairedAccess(identity);
        await using var tunnel = new HttpTunnelClient(new HttpTunnelRequest
        {
            LocalUrl = acpServer.LocalUrl,
            PublicUrl = host.PublicUrl,
            Access = access,
        }, null);
        await tunnel.ConnectAsync(cts.Token);

        var cookieContainer = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookieContainer };
        using var phone = new HttpClient(handler) { BaseAddress = new Uri(tunnel.TunnelUrl!), Timeout = TestData.Timeout };

        // 1. Anonymous request is blocked by PairedAccess
        using var unauth = await phone.GetAsync("/", cts.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        // 2. Pair the mobile browser
        var code = tunnel.CreatePairingUrl().Split("#code=")[1];
        using var pairResp = await phone.PostAsJsonAsync("/_tunnelite/pair", new { code, name = "iPhone 17 Pro" }, cts.Token);
        Assert.Equal(HttpStatusCode.NoContent, pairResp.StatusCode);

        // 3. Load mobile HTML & JS
        var html = await phone.GetStringAsync("/", cts.Token);
        Assert.Contains("ACP Agent Orchestrator", html);
        var js = await phone.GetStringAsync("/app.js", cts.Token);
        Assert.Contains("enqueue_prompt", js);

        // 4. Connect real-time WebSocket over the paired tunnel
        using var ws = new ClientWebSocket();
        ws.Options.Cookies = cookieContainer;
        var wsUri = new Uri(tunnel.TunnelUrl!.Replace("http://", "ws://") + "/ws");
        await ws.ConnectAsync(wsUri, cts.Token);

        var (initFrameBytes, _) = await TestData.ReceiveMessageAsync(ws, cts.Token);
        var initEnv = JsonSerializer.Deserialize(initFrameBytes, AcpGatewayJsonContext.Default.AcpGatewayRealtimeEnvelope);
        Assert.NotNull(initEnv);
        Assert.Equal("state_snapshot", initEnv!.Type);
        Assert.Equal(4, initEnv.State!.Providers.Count);

        // 5. Create an interactive session and start Turn 1 (held open by ControllableTestAdapter)
        using var createRes = await phone.PostAsJsonAsync("/api/action", new AcpGatewayActionRequest
        {
            Action = "create_session",
            Provider = "claude",
            WorkingDirectory = Path.GetTempPath(),
            Prompt = "Refactor auth middleware"
        }, AcpGatewayJsonContext.Default.AcpGatewayActionRequest);
        var createBody = await createRes.Content.ReadFromJsonAsync(AcpGatewayJsonContext.Default.AcpGatewayActionResponse);
        Assert.NotNull(createBody);
        Assert.True(createBody!.Ok);
        var sessionKey = createBody.SessionKey!;
        Assert.True(createBody.Session!.Summary.IsTurnActive);

        // 6. Enqueue two next-turn prompts while Turn 1 is active, then remove the first one
        using var q1Res = await phone.PostAsJsonAsync("/api/action", new AcpGatewayActionRequest
        {
            Action = "enqueue_prompt",
            SessionKey = sessionKey,
            Prompt = "First queued prompt (to remove)"
        }, AcpGatewayJsonContext.Default.AcpGatewayActionRequest);
        var q1Body = await q1Res.Content.ReadFromJsonAsync(AcpGatewayJsonContext.Default.AcpGatewayActionResponse);
        Assert.NotNull(q1Body?.QueuedItem);

        using var q2Res = await phone.PostAsJsonAsync("/api/action", new AcpGatewayActionRequest
        {
            Action = "enqueue_prompt",
            SessionKey = sessionKey,
            Prompt = "Second queued prompt (auto-drains)"
        }, AcpGatewayJsonContext.Default.AcpGatewayActionRequest);
        var q2Body = await q2Res.Content.ReadFromJsonAsync(AcpGatewayJsonContext.Default.AcpGatewayActionResponse);
        Assert.Equal(2, q2Body!.Session!.QueuedPrompts.Count);

        using var rmRes = await phone.PostAsJsonAsync("/api/action", new AcpGatewayActionRequest
        {
            Action = "remove_queued",
            SessionKey = sessionKey,
            QueueItemId = q1Body!.QueuedItem!.QueueItemId
        }, AcpGatewayJsonContext.Default.AcpGatewayActionRequest);
        var rmBody = await rmRes.Content.ReadFromJsonAsync(AcpGatewayJsonContext.Default.AcpGatewayActionResponse);
        Assert.True(rmBody!.Ok);
        Assert.Single(rmBody.Session!.QueuedPrompts);

        // 7. Steer live during Turn 1
        using var steerRes = await phone.PostAsJsonAsync("/api/action", new AcpGatewayActionRequest
        {
            Action = "steer",
            SessionKey = sessionKey,
            Prompt = "Use SemaphoreSlim"
        }, AcpGatewayJsonContext.Default.AcpGatewayActionRequest);
        var steerBody = await steerRes.Content.ReadFromJsonAsync(AcpGatewayJsonContext.Default.AcpGatewayActionResponse);
        Assert.True(steerBody!.Ok);
        Assert.Contains("Use SemaphoreSlim", fakeClaude.LastHandle!.SteeredPrompts);

        // 8. Emit a tool permission request from the agent and approve it from the phone
        fakeClaude.LastHandle.EmitPermissionRequest("perm_99", "Bash", "Run dotnet test?");
        for (var i = 0; i < 20; i++)
        {
            var snap = await phone.GetFromJsonAsync($"/api/state?sessionKey={Uri.EscapeDataString(sessionKey)}", AcpGatewayJsonContext.Default.AcpGatewayStateSnapshot);
            if (snap?.ActiveSession?.PendingPermissions.Count > 0)
            {
                break;
            }
            await Task.Delay(25);
        }

        using var permRes = await phone.PostAsJsonAsync("/api/action", new AcpGatewayActionRequest
        {
            Action = "respond_permission",
            SessionKey = sessionKey,
            PermissionRequestId = "perm_99",
            Approved = true
        }, AcpGatewayJsonContext.Default.AcpGatewayActionRequest);
        var permBody = await permRes.Content.ReadFromJsonAsync(AcpGatewayJsonContext.Default.AcpGatewayActionResponse);
        Assert.True(permBody!.Ok);
        Assert.True(fakeClaude.LastHandle.LastPermissionApproved);
        Assert.Empty(permBody.Session!.PendingPermissions);

        // 9. Complete Turn 1 -> verify the queued prompt auto-drains into Turn 2!
        fakeClaude.LastHandle.CompleteActiveTurn("Turn 1 finished.");
        for (var i = 0; i < 30 && fakeClaude.LastHandle.SentPrompts.Count < 2; i++)
        {
            await Task.Delay(25);
        }
        Assert.Equal(2, fakeClaude.LastHandle.SentPrompts.Count);
        Assert.Equal("Second queued prompt (auto-drains)", fakeClaude.LastHandle.SentPrompts[1]);

        // 10. Interrupt Turn 2 and replace it with an urgent prompt
        using var intRes = await phone.PostAsJsonAsync("/api/action", new AcpGatewayActionRequest
        {
            Action = "interrupt_and_prompt",
            SessionKey = sessionKey,
            Prompt = "Stop and check AGENTS.md"
        }, AcpGatewayJsonContext.Default.AcpGatewayActionRequest);
        var intBody = await intRes.Content.ReadFromJsonAsync(AcpGatewayJsonContext.Default.AcpGatewayActionResponse);
        Assert.True(intBody!.Ok);
        Assert.True(fakeClaude.LastHandle.CancelCount >= 1);
        Assert.Equal("Stop and check AGENTS.md", fakeClaude.LastHandle.SentPrompts[^1]);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
    }

    [Fact]
    public async Task AcpGatewayServer_tails_discovered_transcript_upgrades_to_live_session_and_streams_sse_events()
    {
        var tempTranscript = Path.Combine(Path.GetTempPath(), $"acp-disc-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(
            tempTranscript,
            "{\"step_index\":1,\"source\":\"MODEL\",\"type\":\"PLANNER_RESPONSE\",\"tool_calls\":[{\"name\":\"read_file\",\"args\":{\"Path\":\"\\\"/Users/dev/tunnelite/src/Program.cs\\\"\",\"StartLine\":\"1\",\"EndLine\":\"50\"}}]}\n" +
            "{\"step_index\":2,\"source\":\"MODEL\",\"type\":\"GENERIC\",\"content\":\"1: using System;\"}\n");

        try
        {
            var oldSession = new AcpDiscoveredSession(
                Provider: AgentProviderKind.Antigravity,
                SessionId: "disc-agy-old",
                Title: "Two Days Ago Session",
                WorkingDirectory: Path.GetTempPath(),
                TranscriptPath: tempTranscript,
                UpdatedAt: DateTimeOffset.UtcNow.AddDays(-2),
                CreatedAt: DateTimeOffset.UtcNow.AddDays(-2),
                Model: "gemini-2.5-pro",
                GitBranch: "main",
                MessageCount: 45);

            var activeSession = new AcpDiscoveredSession(
                Provider: AgentProviderKind.Antigravity,
                SessionId: "disc-agy-101",
                Title: "Existing Antigravity Session",
                WorkingDirectory: Path.GetTempPath(),
                TranscriptPath: tempTranscript,
                UpdatedAt: DateTimeOffset.UtcNow,
                CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
                Model: "gemini-2.5-pro",
                GitBranch: "feat/acp",
                MessageCount: 45);

            var historyEvents = new List<AcpEvent>();
            for (var i = 1; i <= 42; i++)
            {
                historyEvents.Add(new AcpTranscriptHistoryEntryEvent(
                    AgentProviderKind.Antigravity,
                    "disc-agy-101",
                    DateTimeOffset.UtcNow.AddMinutes(-50 + i),
                    i % 2 == 1 ? "user" : "assistant",
                    $"Historical message #{i}"));
            }
            historyEvents.Add(new AcpTranscriptHistoryEntryEvent(AgentProviderKind.Antigravity, "disc-agy-101", DateTimeOffset.UtcNow.AddMinutes(-5), "user", "<USER_REQUEST>\nInitial desktop prompt\n</USER_REQUEST>\n<ADDITIONAL_METADATA>\ntime\n</ADDITIONAL_METADATA>"));
            historyEvents.Add(new AcpTranscriptHistoryEntryEvent(AgentProviderKind.Antigravity, "disc-agy-101", DateTimeOffset.UtcNow.AddMinutes(-4), "assistant", "", Thinking: null, ToolName: "read_file"));
            historyEvents.Add(new AcpTranscriptHistoryEntryEvent(AgentProviderKind.Antigravity, "disc-agy-101", DateTimeOffset.UtcNow.AddMinutes(-3), "assistant", "Desktop response from transcript"));

            var fakeAgy = new ControllableTestAdapter(AgentProviderKind.Antigravity, "agy", "Google Antigravity")
            {
                DiscoveredSessions = [oldSession, activeSession],
                TranscriptHistoryEvents = historyEvents
            };

            await using var runtime = new AcpliteRuntime([fakeAgy]);
            await using var orchestrator = new AcpGatewayOrchestrator(scopedProvider: AgentProviderKind.Antigravity, Path.GetTempPath(), runtime);
            await using var acpServer = await AcpGatewayServer.StartNewAsync(orchestrator, ownsOrchestrator: false);

            using var cts = new CancellationTokenSource(TestData.Timeout);
            using var client = new HttpClient { BaseAddress = new Uri(acpServer.LocalUrl), Timeout = TestData.Timeout };

            // 1. Connect SSE stream (/api/events) and verify initial state_snapshot frame sorts active session to top
            using var sseReq = new HttpRequestMessage(HttpMethod.Get, "/api/events");
            using var sseRes = await client.SendAsync(sseReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Assert.Equal(HttpStatusCode.OK, sseRes.StatusCode);
            await using var sseStream = await sseRes.Content.ReadAsStreamAsync(cts.Token);
            using var sseReader = new StreamReader(sseStream, Encoding.UTF8);

            var firstSseLine = await sseReader.ReadLineAsync(cts.Token);
            Assert.NotNull(firstSseLine);
            Assert.StartsWith("data: ", firstSseLine);
            var firstEnv = JsonSerializer.Deserialize(firstSseLine!["data: ".Length..], AcpGatewayJsonContext.Default.AcpGatewayRealtimeEnvelope);
            Assert.NotNull(firstEnv?.State);
            Assert.Equal(2, firstEnv!.State!.Sessions.Count);
            Assert.Equal("agy:disc-agy-101", firstEnv.State.Sessions[0].SessionKey);
            Assert.True(firstEnv.State.Sessions[0].IsActiveNow);
            Assert.False(firstEnv.State.Sessions[1].IsActiveNow);
            Assert.Equal("agy:disc-agy-101", firstEnv.State.ActiveSessionKey);

            // 2. Verify default pagination shows only last 30 items with HasOlderMessages == true
            var state = firstEnv.State;
            Assert.NotNull(state.ActiveSession);
            Assert.False(state.ActiveSession!.Summary.IsLiveAttached);
            Assert.Equal(30, state.ActiveSession.Timeline.Count);
            Assert.Equal(45, state.ActiveSession.TotalTimelineCount);
            Assert.Equal(15, state.ActiveSession.HiddenOlderCount);
            Assert.True(state.ActiveSession.HasOlderMessages);
            Assert.Equal("Desktop response from transcript", state.ActiveSession.Timeline[^1].Text);
            Assert.Equal("tool_call", state.ActiveSession.Timeline[^2].Kind);
            Assert.Equal("read_file", state.ActiveSession.Timeline[^2].ToolName);
            Assert.Equal("src/Program.cs:1-50", state.ActiveSession.Timeline[^2].Text);
            Assert.Equal("1: using System;", state.ActiveSession.Timeline[^2].RawOutput);
            Assert.Equal("Initial desktop prompt", state.ActiveSession.Timeline[^3].Text);

            // 3. Load earlier messages on-demand via load_older action
            using var loadOlderRes = await client.PostAsJsonAsync("/api/action", new AcpGatewayActionRequest
            {
                Action = "load_older",
                SessionKey = "agy:disc-agy-101",
                TimelineLimit = 70
            }, AcpGatewayJsonContext.Default.AcpGatewayActionRequest, cts.Token);
            var loadOlderBody = await loadOlderRes.Content.ReadFromJsonAsync(AcpGatewayJsonContext.Default.AcpGatewayActionResponse, cts.Token);
            Assert.NotNull(loadOlderBody?.Session);
            Assert.Equal(45, loadOlderBody!.Session!.Timeline.Count);
            Assert.Equal(0, loadOlderBody.Session.HiddenOlderCount);
            Assert.False(loadOlderBody.Session.HasOlderMessages);
            Assert.Equal("Historical message #1", loadOlderBody.Session.Timeline[0].Text);

            // 4. Take over the discovered session from mobile -> upgrades from transcript tail to live handle!
            using var sendRes = await client.PostAsJsonAsync("/api/action", new AcpGatewayActionRequest
            {
                Action = "send_prompt",
                SessionKey = "agy:disc-agy-101",
                Prompt = "Take over from mobile phone"
            }, AcpGatewayJsonContext.Default.AcpGatewayActionRequest, cts.Token);
            var sendBody = await sendRes.Content.ReadFromJsonAsync(AcpGatewayJsonContext.Default.AcpGatewayActionResponse, cts.Token);
            Assert.NotNull(sendBody);
            Assert.True(sendBody!.Ok);
            Assert.True(sendBody.Session!.Summary.IsLiveAttached);
            Assert.Contains("Take over from mobile phone", fakeAgy.LastHandle!.SentPrompts);
        }
        finally
        {
            if (File.Exists(tempTranscript))
            {
                File.Delete(tempTranscript);
            }
        }
    }

    [Fact]
    public async Task AcpGatewayServer_handles_provider_exceptions_invalid_json_and_websocket_commands_gracefully()
    {
        var fakeClaude = new ControllableTestAdapter(AgentProviderKind.Claude, "claude", "Claude Code")
        {
            ThrowOnStart = new InvalidOperationException("Claude binary is not installed on this machine.")
        };
        await using var runtime = new AcpliteRuntime([fakeClaude]);
        await using var orchestrator = new AcpGatewayOrchestrator(scopedProvider: null, Path.GetTempPath(), runtime);
        await using var acpServer = await AcpGatewayServer.StartNewAsync(orchestrator, ownsOrchestrator: false);

        using var cts = new CancellationTokenSource(TestData.Timeout);
        using var client = new HttpClient { BaseAddress = new Uri(acpServer.LocalUrl), Timeout = TestData.Timeout };

        // 1. Invalid JSON returns 400 Bad Request with JSON error message
        using var badContent = new StringContent("{not-valid-json", Encoding.UTF8, "application/json");
        using var badRes = await client.PostAsync("/api/action", badContent, cts.Token);
        Assert.Equal(HttpStatusCode.BadRequest, badRes.StatusCode);

        // 2. Provider exception during create_session is caught and returned as 200 OK { ok: false, message: ... }
        using var failRes = await client.PostAsJsonAsync("/api/action", new AcpGatewayActionRequest
        {
            Action = "create_session",
            Provider = "claude",
            Prompt = "Hello"
        }, AcpGatewayJsonContext.Default.AcpGatewayActionRequest, cts.Token);
        Assert.Equal(HttpStatusCode.OK, failRes.StatusCode);
        var failBody = await failRes.Content.ReadFromJsonAsync(AcpGatewayJsonContext.Default.AcpGatewayActionResponse, cts.Token);
        Assert.NotNull(failBody);
        Assert.False(failBody!.Ok);
        Assert.Contains("Claude binary is not installed", failBody.Message);

        // 3. Clear ThrowOnStart and execute action directly over WebSocket (/ws)
        fakeClaude.ThrowOnStart = null;
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(acpServer.LocalUrl.Replace("http://", "ws://") + "/ws"), cts.Token);

        // Read initial snapshot
        _ = await TestData.ReceiveMessageAsync(ws, cts.Token);

        var wsCmdBytes = JsonSerializer.SerializeToUtf8Bytes(new AcpGatewayActionRequest
        {
            Action = "create_session",
            Provider = "claude",
            Prompt = "Created over WebSocket"
        }, AcpGatewayJsonContext.Default.AcpGatewayActionRequest);
        await ws.SendAsync(wsCmdBytes, WebSocketMessageType.Text, endOfMessage: true, cts.Token);

        AcpGatewayRealtimeEnvelope? actionResultEnv = null;
        for (var i = 0; i < 15; i++)
        {
            var (frameBytes, _) = await TestData.ReceiveMessageAsync(ws, cts.Token);
            var env = JsonSerializer.Deserialize(frameBytes, AcpGatewayJsonContext.Default.AcpGatewayRealtimeEnvelope);
            if (env?.Type == "action_result")
            {
                actionResultEnv = env;
                break;
            }
        }

        Assert.NotNull(actionResultEnv);
        Assert.NotNull(actionResultEnv!.SessionKey);
        Assert.Equal("Created over WebSocket", fakeClaude.LastHandle!.SentPrompts[0]);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
    }

    [Trait("Category", "Cli")]
    [CliFact]
    public async Task Cli_acp_subcommand_scopes_provider_and_serves_paired_mobile_orchestrator()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tunnelite-cli-acp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var identityPath = Path.Combine(directory, "desktop.json");

        try
        {
            var cliPath = Environment.GetEnvironmentVariable(CliFactAttribute.EnvironmentVariable)!;
            var info = new ProcessStartInfo(cliPath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in new[] { "acp", "agy", "--publicUrl", host.PublicUrl, "--identity-file", identityPath, "--cwd", directory })
            {
                info.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = info };
            var output = new ConcurrentQueue<string>();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                string? pairingLine = null;
                using var timeout = new CancellationTokenSource(TestData.Timeout);
                while (!timeout.IsCancellationRequested)
                {
                    pairingLine = output.FirstOrDefault(s => s.StartsWith("Pairing URL: ", StringComparison.Ordinal));
                    if (pairingLine is not null) break;
                    if (process.HasExited) throw new InvalidOperationException("CLI exited early: " + string.Join("\n", output));
                    await Task.Delay(50, timeout.Token);
                }

                Assert.NotNull(pairingLine);
                var pairingUrl = pairingLine!["Pairing URL: ".Length..];
                var pairUri = new Uri(pairingUrl);
                var baseUri = new Uri(pairUri.GetLeftPart(UriPartial.Authority));
                var code = pairingUrl.Split("#code=")[1];

                var cookies = new CookieContainer();
                using var handler = new HttpClientHandler { CookieContainer = cookies };
                using var browser = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TestData.Timeout };

                using var pairRes = await browser.PostAsJsonAsync("/_tunnelite/pair", new { code, name = "Pixel 10 Pro" });
                Assert.Equal(HttpStatusCode.NoContent, pairRes.StatusCode);

                var state = await browser.GetFromJsonAsync("/api/state", AcpGatewayJsonContext.Default.AcpGatewayStateSnapshot);
                Assert.NotNull(state);
                Assert.Equal("agy", state!.ScopedProvider);
                Assert.Single(state.Providers);
                Assert.Equal("agy", state.Providers[0].ShortId);

                await process.StandardInput.WriteLineAsync("q");
                await process.StandardInput.FlushAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, process.ExitCode);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class ControllableTestAdapter(AgentProviderKind provider, string shortId, string displayName) : IAcpProviderAdapter
    {
        public AgentProviderKind Provider { get; } = provider;
        public string ShortId { get; } = shortId;
        public string DisplayName { get; } = displayName;
        public string DefaultBinaryName { get; } = shortId;
        public bool IsNativeAcp => false;
        public bool SupportsLiveSteering => true;
        public bool SupportsSessionDiscovery => true;
        public bool SupportsUsageProbe => true;

        public ControllableSessionHandle? LastHandle { get; private set; }
        public IReadOnlyList<AcpDiscoveredSession> DiscoveredSessions { get; set; } = [];
        public IReadOnlyList<AcpEvent> TranscriptHistoryEvents { get; set; } = [];
        public Exception? ThrowOnStart { get; set; }

        public Task<AcpProviderDescriptor> DiscoverInstalledAsync(string? binaryPathOverride = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new AcpProviderDescriptor(Provider, ShortId, DisplayName, DefaultBinaryName, true, $"/usr/local/bin/{ShortId}", "1.0.0", false, true, true, true));

        public Task<IReadOnlyList<AcpDiscoveredSession>> DiscoverSessionsAsync(int maxResults = 50, CancellationToken cancellationToken = default)
            => Task.FromResult(DiscoveredSessions);

        public async IAsyncEnumerable<AcpEvent> TailSessionTranscriptAsync(AcpDiscoveredSession session, bool includeHistory = true, bool followLive = true, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (includeHistory && session.SessionId == "disc-agy-101")
            {
                foreach (var ev in TranscriptHistoryEvents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return ev;
                }
            }

            if (followLive)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<IAcpSessionHandle> StartSessionAsync(AcpSessionConfig config, CancellationToken cancellationToken = default)
        {
            if (ThrowOnStart is not null)
            {
                throw ThrowOnStart;
            }
            var handle = new ControllableSessionHandle(Provider, config);
            LastHandle = handle;
            await handle.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return handle;
        }

        public Task<AcpUsageSnapshot> ProbeUsageAsync(string? binaryPathOverride = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new AcpUsageSnapshot(Provider, DateTimeOffset.UtcNow, PlanType: "Pro", FiveHourPercentUsed: 25.0));
    }

    private sealed class ControllableSessionHandle : AcpSessionHandleBase
    {
        public List<string> SentPrompts { get; } = [];
        public List<string> SteeredPrompts { get; } = [];
        public int CancelCount { get; private set; }
        public bool? LastPermissionApproved { get; private set; }

        public ControllableSessionHandle(AgentProviderKind provider, AcpSessionConfig config)
            : base(provider, config, Stream.Null, Stream.Null, process: null)
        {
            SessionId = config.SessionId ?? "test-sess-" + Guid.NewGuid().ToString("N")[..8];
        }

        public override Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            PublishEvent(new AcpSessionStartedEvent(Provider, SessionId, DateTimeOffset.UtcNow, Config.Model ?? "claude-sonnet-4-5", Config.WorkingDirectory));
            return Task.CompletedTask;
        }

        protected override Task SendPromptCoreAsync(string prompt, CancellationToken cancellationToken = default)
        {
            SentPrompts.Add(prompt);
            PublishEvent(new AcpThinkingDeltaEvent(Provider, SessionId, DateTimeOffset.UtcNow, $"Thinking about: {prompt}"));
            return Task.CompletedTask;
        }

        public override Task<bool> SteerAsync(string steeringPrompt, CancellationToken cancellationToken = default)
        {
            SteeredPrompts.Add(steeringPrompt);
            return Task.FromResult(true);
        }

        public override Task RespondPermissionAsync(string requestId, bool approved, string? selectedOptionId = null, CancellationToken cancellationToken = default)
        {
            LastPermissionApproved = approved;
            return Task.CompletedTask;
        }

        public override Task SetModelAsync(string modelId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public override Task CancelTurnAsync(CancellationToken cancellationToken = default)
        {
            CancelCount++;
            PublishEvent(new AcpTurnCompletedEvent(Provider, SessionId, DateTimeOffset.UtcNow, "cancelled"));
            return Task.CompletedTask;
        }

        public void EmitPermissionRequest(string requestId, string toolName, string title)
        {
            PublishEvent(new AcpPermissionRequestEvent(
                Provider,
                SessionId,
                DateTimeOffset.UtcNow,
                requestId,
                toolName,
                title,
                "dotnet test",
                "{\"cmd\":\"dotnet test\"}",
                [new AcpPermissionOption("allow", "Allow", "allow_once"), new AcpPermissionOption("deny", "Deny", "reject_once")]));
        }

        public void CompleteActiveTurn(string responseText)
        {
            var now = DateTimeOffset.UtcNow;
            PublishEvent(new AcpAssistantTextDeltaEvent(Provider, SessionId, now, responseText));
            PublishEvent(new AcpTurnCompletedEvent(Provider, SessionId, now, "end_turn", InputTokens: 100, OutputTokens: 50, CostUsd: 0.01m, DurationMs: 500));
        }
    }
}
