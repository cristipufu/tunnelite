using System.CommandLine.Invocation;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using Tunnelite.Sdk;

namespace Tunnelite.Client;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var root = new RootCommand("CLI tool to create a tunnel to a local server.");
        Configure(root, pairedMode: false);
        var serve = new Command("serve", "Serve a local web app to paired browsers with a QR code.");
        Configure(serve, pairedMode: true);
        root.AddCommand(serve);
        return await root.InvokeAsync(args);
    }

    private static void Configure(Command command, bool pairedMode)
    {
        var local = new Argument<string>("localUrl", "The local URL to tunnel to.");
        var relay = new Option<string>("--publicUrl", () => "https://tunnelite.com", "The relay server URL.");
        var log = new Option<LogLevel?>("--log", () => null, "Client log verbosity.");
        var pair = new Option<bool>("--pair", "Require QR pairing before allowing browser access.");
        var name = new Option<string>("--name", () => "default", "Desktop profile name; the same profile keeps the same address.");
        var identity = new Option<string?>("--identity-file", "Desktop profile file (identity and paired devices).");
        var qr = new Option<bool>("--qr", "Display a QR code for an ordinary public tunnel.");
        var insecureLocal = new Option<bool>("--insecure-local", "Allow an untrusted HTTPS certificate only on a loopback local server.");
        command.AddArgument(local);
        foreach (var option in new Option[] { relay, log, pair, name, identity, qr, insecureLocal }) command.AddOption(option);
        command.SetHandler(async (InvocationContext invocation) =>
        {
            try
            {
                await RunAsync(invocation.ParseResult.GetValueForArgument(local), invocation.ParseResult.GetValueForOption(relay)!,
                    invocation.ParseResult.GetValueForOption(log), pairedMode || invocation.ParseResult.GetValueForOption(pair),
                    invocation.ParseResult.GetValueForOption(name)!, invocation.ParseResult.GetValueForOption(identity),
                    invocation.ParseResult.GetValueForOption(qr), invocation.ParseResult.GetValueForOption(insecureLocal), invocation.GetCancellationToken());
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                AnsiConsole.Write(new Text(TerminalText.Clean(exception.Message) + Environment.NewLine, new Style(Color.Red)));
                invocation.ExitCode = 1;
            }
        });
    }

    private static async Task RunAsync(string localUrl, string publicUrl, LogLevel? logLevel, bool pair,
        string profileName, string? identityFile, bool qr, bool insecureLocal, CancellationToken cancellationToken)
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
        client.LogFailedRequest += (method, path) => display.Error("Failed: " + TerminalText.Request(method, path));
        client.LogError += display.Error;
        client.LogException += exception => display.Error(exception.Message);
        var readyVersion = 0;
        client.Connected += () => { Interlocked.Increment(ref readyVersion); return Task.CompletedTask; };
        var connecting = client is HttpTunnelClient http ? http.ConnectAsync(lifetime.Token) : client.ConnectAsync().WaitAsync(lifetime.Token);
        try
        {
            await display.RunAsync(token => RunMainLoop(client, connecting, display, access, qr,
                () => Volatile.Read(ref readyVersion), token), lifetime.Token);
        }
        finally
        {
            await lifetime.CancelAsync();
            if (client is IAsyncDisposable disposable) await disposable.DisposeAsync();
            try { await connecting; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    private static async Task RunMainLoop(ITunnelClient client, Task connecting, CliDisplay display,
        PairedAccess? access, bool qr, Func<int> readyVersion, CancellationToken cancellationToken)
    {
        var initialReady = false;
        var observedVersion = -1;
        var lineInput = Console.IsInputRedirected;
        Task<string?>? input = lineInput ? Task.Run(Console.ReadLine) : null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!initialReady && connecting.IsCompleted) { await connecting; initialReady = true; }
            var status = initialReady ? client.Connection.State == HubConnectionState.Connected && client.TunnelUrl is not null
                ? "Connected" : "Reconnecting" : "Connecting";
            if (status != display.View.Status) display.SetView(display.View with { Status = status, Qr = null });
            if (status == "Connected" && (observedVersion != readyVersion() || display.View.PublicUrl != client.TunnelUrl))
            {
                RefreshQr(client, display, access, qr);
                observedVersion = readyVersion();
            }
            if (access is not null)
            {
                var devices = access.State.Devices;
                display.SetView(display.View with { Devices = devices, SelectedDevice = Math.Clamp(display.View.SelectedDevice, 0, Math.Max(0, devices.Count - 1)) });
            }
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
            action = action?.Trim();
            if (action is "q" or "quit") return;
            try
            {
                if (action is "c" or "clear") { display.ClearActivity(); display.Notice("Activity cleared."); }
                if (action is "p" or "pair" && (access is not null || qr)) RefreshQr(client, display, access, qr);
                if (action == "back") display.SetView(display.View with { Panel = DevicePanel.Closed, Notice = "" });
                if (action is "d" or "devices" && access is not null)
                    display.ShowDevices(display.View.Panel == DevicePanel.List ? DevicePanel.Closed : DevicePanel.List, access.State.Devices);
                if (action == "r" && access is not null) display.ShowDevices(DevicePanel.Revoke, access.State.Devices);
                if (action is "up" or "down" && display.View.Panel != DevicePanel.Closed)
                {
                    var view = display.View;
                    var count = view.Devices?.Count ?? 0;
                    var selected = Math.Clamp(view.SelectedDevice + (action == "up" ? -1 : 1), 0, Math.Max(0, count - 1));
                    display.SetView(view with { SelectedDevice = selected });
                    if (!display.Live && count > 0) display.Notice("Selected: " + TerminalText.Clean(view.Devices![selected].Name));
                }
                if (action == "select" && display.View.Panel == DevicePanel.Revoke && access is not null)
                {
                    var devices = access.State.Devices;
                    if (display.View.SelectedDevice < devices.Count) action = "revoke " + devices[display.View.SelectedDevice].Id;
                }
                if (action?.StartsWith("revoke ", StringComparison.Ordinal) == true && access is not null)
                {
                    if (Guid.TryParse(action[7..].Trim(), out var id))
                    {
                        await access.RevokeAsync(id, cancellationToken);
                        display.SetView(display.View with { Panel = DevicePanel.List, Devices = access.State.Devices });
                        display.Notice("Device access revoked.");
                    }
                    else display.Notice("Use revoke <device-id>.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { display.Error(exception.Message); display.Notice("Could not complete that action. Try again."); }
            display.RefreshPlain();
            await Task.Delay(100, cancellationToken);
        }
    }

    private static void RefreshQr(ITunnelClient client, CliDisplay display, PairedAccess? access, bool qr)
    {
        var url = client.TunnelUrl;
        if (url is null) { display.Notice("The tunnel is reconnecting. Try again when connected."); return; }
        var qrUrl = access is not null ? ((HttpTunnelClient)client).CreatePairingUrl() : qr ? url : null;
        display.Ready(url, qrUrl);
    }
}
