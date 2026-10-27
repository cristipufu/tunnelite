using System.CommandLine.Invocation;
using Acplite.Pty;
using Acplite.Sdk;
using Acplite.Sdk.Protocol;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using Tunnelite.Acp;
using Tunnelite.Sdk;

namespace Tunnelite.Client;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length >= 2 &&
            string.Equals(args[0], "acp-pty", StringComparison.OrdinalIgnoreCase) &&
            !IsHelpFlag(args[1]) &&
            !string.Equals(args[1], "list", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(args[1], "--list", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteDirectPtyPassThroughAsync(args).ConfigureAwait(false);
        }

        var root = new RootCommand("CLI tool to create a tunnel to a local server.");
        Configure(root, pairedMode: false);
        var serve = new Command("serve", "Serve a local web app to paired browsers with a QR code.");
        Configure(serve, pairedMode: true);
        root.AddCommand(serve);
        var acp = new Command("acp", "Bridge local coding agents (claude, codex, grok, agy) to paired mobile browsers.");
        ConfigureAcp(acp);
        root.AddCommand(acp);
        root.AddCommand(BuildAcpPtyCommand());
        return await root.InvokeAsync(args);
    }

    private static bool IsHelpFlag(string arg)
        => string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(arg, "-?", StringComparison.OrdinalIgnoreCase);

    private static async Task<int> ExecuteDirectPtyPassThroughAsync(string[] args)
    {
        try
        {
            var rawAgent = args[1].Trim();
            string providerShortId;
            string? resolvedBinaryPath = null;

            if (AgentProviderParser.TryParse(rawAgent, out var kind))
            {
                providerShortId = kind.ToShortId();
                await using var runtime = new AcpliteRuntime(externalSessionBridge: PtySessionBridge.Instance);
                var descriptor = await runtime.GetAdapter(kind).DiscoverInstalledAsync().ConfigureAwait(false);
                if (descriptor.IsInstalled && !string.IsNullOrWhiteSpace(descriptor.ResolvedBinaryPath))
                {
                    resolvedBinaryPath = descriptor.ResolvedBinaryPath;
                }
            }
            else
            {
                providerShortId = rawAgent.ToLowerInvariant();
            }

            resolvedBinaryPath ??= PtyFactory.ResolveExecutableOnPath(rawAgent);
            if (string.IsNullOrWhiteSpace(resolvedBinaryPath))
            {
                AnsiConsole.Write(new Text($"Could not find '{TerminalText.Clean(rawAgent)}' CLI executable on PATH.\n", new Style(Color.Red)));
                return 1;
            }

            var startIdx = 2;
            if (startIdx < args.Length && string.Equals(args[startIdx], "--", StringComparison.Ordinal))
            {
                startIdx++;
            }

            var forwardedArgs = startIdx < args.Length
                ? args[startIdx..]
                : [];

            return await PtySessionRunner.RunInteractiveAsync(
                providerShortId,
                resolvedBinaryPath,
                forwardedArgs).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AnsiConsole.Write(new Text(TerminalText.Clean(ex.Message) + Environment.NewLine, new Style(Color.Red)));
            return 1;
        }
    }

    private static Command BuildAcpPtyCommand()
    {
        var cmd = new Command("acp-pty", "Run an interactive coding agent inside a transparent pseudo-terminal (PTY) with a local control socket, or list active PTY sessions.");
        var providerArg = new Argument<string?>("provider", () => "list", "Coding agent to launch (claude, codex, grok, agy) or 'list' to inspect active PTY tabs.");
        var listOpt = new Option<bool>("--list", "List active PTY sessions registered on this machine.");
        cmd.AddArgument(providerArg);
        cmd.AddOption(listOpt);
        SetSafeCommandHandler(cmd, async invocation =>
        {
            await RenderActivePtySessionsTableAsync(invocation.GetCancellationToken()).ConfigureAwait(false);
        });
        return cmd;
    }

    private static async Task RenderActivePtySessionsTableAsync(CancellationToken cancellationToken)
    {
        var active = await PtySessionRegistry.ListActiveSessionsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (active.Count == 0)
        {
            AnsiConsole.Write(new Text("No active PTY sessions found.\n", new Style(Color.Grey)));
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("PID")
            .AddColumn("Child PID")
            .AddColumn("Provider")
            .AddColumn("Session ID")
            .AddColumn("Workspace");

        foreach (var s in active)
        {
            table.AddRow(
                new Text(s.Pid.ToString()),
                new Text(s.ChildPid.ToString()),
                new Text(TerminalText.Clean(s.Provider), new Style(Color.Cyan1)),
                new Text(TerminalText.Clean(s.SessionId ?? "-")),
                new Text(TerminalText.Clean(s.WorkingDirectory, 60)));
        }

        AnsiConsole.Write(table);
    }

    private sealed record CommonCliOptions(
        Option<string> Relay,
        Option<LogLevel?> Log,
        Option<string> Name,
        Option<string?> Identity)
    {
        public static CommonCliOptions AttachTo(Command command)
        {
            var options = new CommonCliOptions(
                new Option<string>("--publicUrl", () => "https://tunnelite.com", "The relay server URL."),
                new Option<LogLevel?>("--log", () => null, "Client log verbosity."),
                new Option<string>("--name", () => "default", "Desktop profile name; the same profile keeps the same address."),
                new Option<string?>("--identity-file", "Desktop profile file (identity and paired devices)."));

            command.AddOption(options.Relay);
            command.AddOption(options.Log);
            command.AddOption(options.Name);
            command.AddOption(options.Identity);
            return options;
        }
    }

    private static void SetSafeCommandHandler(Command command, Func<InvocationContext, Task> action)
    {
        command.SetHandler(async (InvocationContext invocation) =>
        {
            try
            {
                await action(invocation);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                AnsiConsole.Write(new Text(TerminalText.Clean(exception.Message) + Environment.NewLine, new Style(Color.Red)));
                invocation.ExitCode = 1;
            }
        });
    }

    private static void ConfigureAcp(Command command)
    {
        var provider = new Argument<string?>("provider", () => null, "Optional coding agent filter: claude, codex, grok, or agy (default: all).");
        var cwd = new Option<string?>("--cwd", () => null, "Default working directory for new agent sessions.");
        var installHooks = new Option<bool>("--install-hooks", "Install transparent PTY shell hooks so terminal agent sessions can be controlled and approved from mobile.");
        var uninstallHooks = new Option<bool>("--uninstall-hooks", "Remove transparent PTY shell hooks from your shell profile.");
        var hooksStatus = new Option<bool>("--hooks-status", "Inspect PTY shell hook installation status and active PTY sessions.");
        var shellOpt = new Option<string?>("--shell", () => null, "Optional shell override for --install-hooks / --uninstall-hooks (zsh, bash, fish, pwsh, powershell).");
        var agentsOpt = new Option<string?>("--agents", () => null, "Optional comma-separated list of agent commands to wrap with --install-hooks (default: claude,codex,grok,agy).");
        command.AddArgument(provider);
        var common = CommonCliOptions.AttachTo(command);
        command.AddOption(cwd);
        command.AddOption(installHooks);
        command.AddOption(uninstallHooks);
        command.AddOption(hooksStatus);
        command.AddOption(shellOpt);
        command.AddOption(agentsOpt);

        SetSafeCommandHandler(command, async invocation =>
        {
            var ct = invocation.GetCancellationToken();
            var doInstall = invocation.ParseResult.GetValueForOption(installHooks);
            var doUninstall = invocation.ParseResult.GetValueForOption(uninstallHooks);
            var doStatus = invocation.ParseResult.GetValueForOption(hooksStatus);
            var shell = invocation.ParseResult.GetValueForOption(shellOpt);
            var agentsCsv = invocation.ParseResult.GetValueForOption(agentsOpt);

            if (doInstall || doUninstall || doStatus)
            {
                await HandleAcpHooksFlagAsync(doInstall, doUninstall, doStatus, shell, agentsCsv, ct).ConfigureAwait(false);
                return;
            }

            await RunAcpAsync(
                invocation.ParseResult.GetValueForArgument(provider),
                invocation.ParseResult.GetValueForOption(common.Relay)!,
                invocation.ParseResult.GetValueForOption(common.Log),
                invocation.ParseResult.GetValueForOption(common.Name)!,
                invocation.ParseResult.GetValueForOption(common.Identity),
                invocation.ParseResult.GetValueForOption(cwd),
                ct).ConfigureAwait(false);
        });
    }

    private static async Task HandleAcpHooksFlagAsync(
        bool install,
        bool uninstall,
        bool status,
        string? shell,
        string? agentsCsv,
        CancellationToken cancellationToken)
    {
        if (install)
        {
            var providers = ShellHookInstaller.ParseProvidersList(agentsCsv);
            var result = await ShellHookInstaller.InstallAsync(
                shell,
                wrapperCommand: "tunnelite acp-pty",
                providers: providers,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            AnsiConsole.Write(new Text(
                $"Installed PTY shell hooks ({string.Join(", ", providers)}) for {TerminalText.Clean(result.ShellName)} in {TerminalText.Clean(result.ProfilePath)}\n",
                new Style(Color.Green)));
            return;
        }

        if (uninstall)
        {
            var result = await ShellHookInstaller.UninstallAsync(
                shell,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            AnsiConsole.Write(new Text(
                $"Removed PTY shell hooks for {TerminalText.Clean(result.ShellName)} from {TerminalText.Clean(result.ProfilePath)}\n",
                new Style(Color.Yellow)));
            return;
        }

        if (status)
        {
            var statuses = await ShellHookInstaller.GetAllStatusesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Shell")
                .AddColumn("Installed")
                .AddColumn("Profile Path");

            foreach (var s in statuses)
            {
                table.AddRow(
                    new Text(TerminalText.Clean(s.ShellName), new Style(Color.Cyan1)),
                    new Text(s.IsInstalled ? "Yes" : "No", new Style(s.IsInstalled ? Color.Green : Color.Grey)),
                    new Text(TerminalText.Clean(s.ProfilePath)));
            }

            AnsiConsole.Write(table);
            await RenderActivePtySessionsTableAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RunAcpAsync(
        string? rawProvider,
        string publicUrl,
        LogLevel? logLevel,
        string profileName,
        string? identityFile,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        AgentProviderKind? scopedProvider = null;
        if (!string.IsNullOrWhiteSpace(rawProvider))
        {
            if (!AgentProviderParser.TryParse(rawProvider, out var parsed) || parsed == AgentProviderKind.GenericAcp)
                throw new ArgumentException("Use claude, codex, grok, or agy as the coding agent provider.");
            scopedProvider = parsed;
        }

        await using var orchestrator = new AcpGatewayOrchestrator(scopedProvider, workingDirectory);
        await using var acpServer = await AcpGatewayServer.StartNewAsync(orchestrator, ownsOrchestrator: false, cancellationToken);
        await RunAsync(acpServer.LocalUrl, publicUrl, logLevel, pair: true, profileName, identityFile, insecureLocal: false, cancellationToken);
    }

    private static void Configure(Command command, bool pairedMode)
    {
        var local = new Argument<string>("localUrl", "The local URL to tunnel to.");
        var pair = new Option<bool>("--pair", "Require QR pairing before allowing browser access.");
        var insecureLocal = new Option<bool>("--insecure-local", "Allow an untrusted HTTPS certificate only on a loopback local server.");
        command.AddArgument(local);
        var common = CommonCliOptions.AttachTo(command);
        command.AddOption(pair);
        command.AddOption(insecureLocal);

        SetSafeCommandHandler(command, invocation =>
            RunAsync(
                invocation.ParseResult.GetValueForArgument(local),
                invocation.ParseResult.GetValueForOption(common.Relay)!,
                invocation.ParseResult.GetValueForOption(common.Log),
                pairedMode || invocation.ParseResult.GetValueForOption(pair),
                invocation.ParseResult.GetValueForOption(common.Name)!,
                invocation.ParseResult.GetValueForOption(common.Identity),
                invocation.ParseResult.GetValueForOption(insecureLocal),
                invocation.GetCancellationToken()));
    }

    private static async Task RunAsync(string localUrl, string publicUrl, LogLevel? logLevel, bool pair,
        string profileName, string? identityFile, bool insecureLocal, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(localUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "tcp"))
            throw new ArgumentException("Use an absolute http://, https:// or tcp:// local URL.");
        publicUrl = publicUrl.TrimEnd('/');
        if (pair && uri.Scheme == "tcp") throw new ArgumentException("Pairing is available for HTTP(S) web apps.");
        if (pair && (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var relay)
            || (relay.Scheme != "https" && !(relay.Scheme == "http" && relay.IsLoopback))))
            throw new ArgumentException("Paired mode requires an HTTPS relay.");
        if (!pair && identityFile is not null) throw new ArgumentException("--identity-file requires serve or --pair.");
        await using var profile = pair ? await DesktopProfile.OpenAsync(identityFile, profileName, publicUrl, cancellationToken) : null;
        using var access = profile is null ? null : new PairedAccess(profile.Identity, profile.Access, profile.SaveAsync);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var live = !Console.IsOutputRedirected && AnsiConsole.Profile.Capabilities.Ansi && !logLevel.HasValue
            && Environment.GetEnvironmentVariable("TERM") != "dumb";
        var console = live ? AnsiConsole.Console : AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No,
        });
        var display = new CliDisplay(console, localUrl, pair, live);
        ITunnelClient client = uri.Scheme == "tcp"
            ? new TcpTunnelClient(new TcpTunnelRequest
            {
                ClientId = Guid.NewGuid(), LocalUrl = localUrl, PublicUrl = publicUrl, Host = uri.Host, LocalPort = uri.Port,
            }, logLevel)
            : new HttpTunnelClient(new HttpTunnelRequest
            {
                ClientId = Guid.NewGuid(), LocalUrl = localUrl, PublicUrl = publicUrl,
                Access = access!, AllowUntrustedLocalCertificates = insecureLocal ? true : null,
            }, logLevel);
        client.LogRequest += display.Request;
        client.LogFailedRequest += display.Activity.Failure;
        client.LogError += display.Error;
        client.LogException += exception => display.Error(exception.Message);
        var readyVersion = 0;
        client.Connected += () => { Interlocked.Increment(ref readyVersion); return Task.CompletedTask; };
        var connecting = client.ConnectAsync(lifetime.Token);
        try
        {
            await display.RunAsync(token => RunMainLoop(client, connecting, display, access,
                () => Volatile.Read(ref readyVersion), token), lifetime.Token);
        }
        finally
        {
            await lifetime.CancelAsync();
            if (client is IAsyncDisposable disposable) await disposable.DisposeAsync();
            try { await connecting; }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
        }
    }

    private static async Task RunMainLoop(ITunnelClient client, Task connecting, CliDisplay display,
        PairedAccess? access, Func<int> readyVersion, CancellationToken cancellationToken)
    {
        var initialReady = false;
        var observedVersion = -1;
        var lineInput = Console.IsInputRedirected;
        Task<string?>? input = lineInput ? Task.Run(Console.ReadLine) : null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (initialReady, observedVersion) = await UpdateConnectionStatusAsync(
                client, connecting, display, access, readyVersion, initialReady, observedVersion);
            (var action, lineInput, input) = await ReadActionAsync(lineInput, input);
            if (action is "q" or "quit") return;
            if (!string.IsNullOrEmpty(action))
                await ExecuteActionAsync(action, client, display, access, cancellationToken);
            display.RefreshPlain();
            await Task.Delay(100, cancellationToken);
        }
    }

    private static async Task<(bool InitialReady, int ObservedVersion)> UpdateConnectionStatusAsync(
        ITunnelClient client, Task connecting, CliDisplay display, PairedAccess? access,
        Func<int> readyVersion, bool initialReady, int observedVersion)
    {
        if (!initialReady && connecting.IsCompleted) { await connecting; initialReady = true; }
        var status = initialReady ? client.Connection.State == HubConnectionState.Connected && client.TunnelUrl is not null
            ? "Connected" : "Reconnecting" : "Connecting";
        if (status != display.View.Status) display.SetView(display.View with { Status = status, Qr = null });
        if (status == "Connected" && (observedVersion != readyVersion() || display.View.PublicUrl != client.TunnelUrl))
        {
            RefreshQr(client, display, access);
            observedVersion = readyVersion();
        }
        if (access is not null) display.SetView(display.View.WithDevices(access.State.Devices));
        return (initialReady, observedVersion);
    }

    private static async Task<(string? Action, bool LineInput, Task<string?>? Input)> ReadActionAsync(
        bool lineInput, Task<string?>? input)
    {
        string? action = null;
        if (input?.IsCompleted == true)
        {
            action = await input;
            input = action is null ? null : Task.Run(Console.ReadLine);
        }
        else if (!lineInput)
        {
            try
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    action = key.Key switch
                    {
                        ConsoleKey.UpArrow => "up", ConsoleKey.DownArrow => "down",
                        ConsoleKey.Escape => "back", ConsoleKey.Enter => "select",
                        _ => key.KeyChar.ToString().ToLowerInvariant(),
                    };
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or PlatformNotSupportedException)
            { lineInput = true; input = Task.Run(Console.ReadLine); }
        }
        return (action?.Trim(), lineInput, input);
    }

    private static async Task ExecuteActionAsync(string action, ITunnelClient client, CliDisplay display,
        PairedAccess? access, CancellationToken cancellationToken)
    {
        try
        {
            if (action is "c" or "clear") { display.ClearActivity(); display.Notice("Activity cleared."); return; }
            if (action is "p" or "pair" && access is not null) { RefreshQr(client, display, access); return; }
            if (action == "back") { display.SetView(display.View with { Panel = DevicePanel.Closed, Notice = "" }); return; }
            if (action is "d" or "devices" && access is not null)
            {
                display.ShowDevices(display.View.Panel == DevicePanel.List ? DevicePanel.Closed : DevicePanel.List, access.State.Devices);
                return;
            }
            if (action == "r" && access is not null) { display.ShowDevices(DevicePanel.Revoke, access.State.Devices); return; }
            if (action is "up" or "down" && display.View.Panel != DevicePanel.Closed)
            {
                MoveDeviceSelection(display, action == "up" ? -1 : 1);
                return;
            }
            if (action == "select" && display.View.Panel == DevicePanel.Revoke && access is not null)
            {
                var devices = display.View.Devices ?? [];
                if (display.View.SelectedDevice >= 0 && display.View.SelectedDevice < devices.Count)
                    action = "revoke " + devices[display.View.SelectedDevice].Id;
            }
            if (action.StartsWith("revoke ", StringComparison.Ordinal) && access is not null)
                await RevokeDeviceAsync(action[7..].Trim(), display, access, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { display.Error(exception.Message); display.Notice("Could not complete that action. Try again."); }
    }

    private static void MoveDeviceSelection(CliDisplay display, int delta)
    {
        var view = display.View;
        var count = view.Devices?.Count ?? 0;
        var selected = Math.Clamp(view.SelectedDevice + delta, 0, Math.Max(0, count - 1));
        display.SetView(view with { SelectedDevice = selected });
        if (!display.Live && count > 0) display.Notice("Selected: " + TerminalText.Clean(view.Devices![selected].Name));
    }

    private static async Task RevokeDeviceAsync(string rawId, CliDisplay display, PairedAccess access,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(rawId, out var id))
        {
            await access.RevokeAsync(id, cancellationToken);
            display.SetView(display.View with { Panel = DevicePanel.List, Devices = access.State.Devices });
            display.Notice("Device access revoked.");
        }
        else display.Notice("Use revoke <device-id>.");
    }

    private static void RefreshQr(ITunnelClient client, CliDisplay display, PairedAccess? access)
    {
        var url = client.TunnelUrl;
        if (url is null) { display.Notice("The tunnel is reconnecting. Try again when connected."); return; }
        string? qrUrl = null;
        if (client is HttpTunnelClient http) qrUrl = access is null ? url : http.CreatePairingUrl();
        display.Ready(url, qrUrl);
    }
}
