using QRCoder;
using System.CommandLine.Invocation;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using Tunnelite.Sdk;

namespace Tunnelite.Client;

public class Program
{
    private static ITunnelClient? Client;
    private static readonly Guid ClientId = Guid.NewGuid();

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
                AnsiConsole.MarkupLine("[red]" + Markup.Escape(exception.Message) + "[/]");
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
        try
        {
            await AnsiConsole.Status().StartAsync("Initializing tunnel...", async status =>
            {
                status.Spinner(Spinner.Known.Dots);
                if (uri.Scheme == "tcp") await InitializeTcpTunnel(localUrl, publicUrl, uri, logLevel, status);
                else await InitializeHttpTunnel(localUrl, publicUrl, logLevel, status, access, insecureLocal, cancellationToken);
            });
            await RunMainLoop(localUrl, access, qr, cancellationToken);
        }
        finally
        {
            if (Client is IAsyncDisposable disposable) await disposable.DisposeAsync();
            Client = null;
        }
    }

    private static async Task InitializeTcpTunnel(string localUrl, string publicUrl, Uri uri, LogLevel? logLevel, StatusContext ctx)
    {
        var tcpTunnel = new TcpTunnelRequest
        {
            ClientId = ClientId,
            LocalUrl = localUrl,
            PublicUrl = publicUrl,
            Host = uri.Host,
            LocalPort = uri.Port,
        };

        ctx.Status("Connecting to TCP tunnel...");

        Client = new TcpTunnelClient(tcpTunnel, logLevel);

        AddLogging(Client);

        await Client.ConnectAsync();

        ctx.Status("TCP tunnel established.");
    }

    private static async Task InitializeHttpTunnel(string localUrl, string publicUrl, LogLevel? logLevel, StatusContext ctx, PairedAccess? access, bool insecureLocal, CancellationToken cancellationToken)
    {
        var httpTunnel = new HttpTunnelRequest
        {
            Access = access!,
            AllowUntrustedLocalCertificates = insecureLocal ? true : null,
            ClientId = ClientId,
            LocalUrl = localUrl,
            PublicUrl = publicUrl,
        };

        ctx.Status("Connecting to HTTP tunnel...");

        Client = new HttpTunnelClient(httpTunnel, logLevel);

        AddLogging(Client);

        await ((HttpTunnelClient)Client).ConnectAsync(cancellationToken);

        ctx.Status("HTTP tunnel established.");
    }

    private static async Task RunMainLoop(string localUrl, PairedAccess? access, bool qr, CancellationToken cancellationToken)
    {
        if (Client is null) return;
        var table = WriteStatusTable(localUrl, Client.TunnelUrl, "green", "Connected");
        ShowQr(access, qr);
        Client.Connected += () =>
        {
            table = WriteStatusTable(localUrl, Client.TunnelUrl, "green", "Connected");
            ShowQr(access, qr);
            return Task.CompletedTask;
        };
        Client.Connection.Reconnecting += _ =>
        {
            table = WriteStatusTable(localUrl, Client.TunnelUrl, "yellow", "Reconnecting");
            return Task.CompletedTask;
        };
        Client.Connection.Closed += _ =>
        {
            table = WriteStatusTable(localUrl, Client.TunnelUrl, "red", "Disconnected");
            return Task.CompletedTask;
        };
        // Console's synchronized reader can block inside ReadLineAsync on Windows and Unix.
        Task<string?>? input = Console.IsInputRedirected ? Task.Run(Console.ReadLine) : null;
        while (!cancellationToken.IsCancellationRequested)
        {
            string? action = null;
            if (input?.IsCompleted == true)
            {
                action = await input;
                input = action is null ? null : Task.Run(Console.ReadLine);
            }
            else if (!Console.IsInputRedirected && Console.KeyAvailable)
            {
                action = Console.ReadKey(true).KeyChar.ToString();
                if (action == "r" && access is not null && access.State.Devices.Count > 0)
                {
                    var choices = access.State.Devices.Select(d => $"{d.Id} {d.Name}").Prepend("Cancel");
                    var selected = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Revoke which device?")
                        .UseConverter(Markup.Escape).AddChoices(choices));
                    action = selected == "Cancel" ? null : "revoke " + selected.Split(' ')[0];
                }
            }
            if (action is "q" or "quit") return;
            if (action is "c" or "clear") { ClearScreen(); AnsiConsole.Write(table); WriteHelp(); }
            if (action is "p" or "pair") ShowQr(access, qr);
            if (action is "d" or "devices")
                foreach (var device in access?.State.Devices ?? []) AnsiConsole.WriteLine($"{device.Id}  {device.Name}  expires {device.ExpiresAt:u}");
            if (action?.StartsWith("revoke ", StringComparison.Ordinal) == true && access is not null)
            {
                if (Guid.TryParse(action[7..].Trim(), out var id)) { await access.RevokeAsync(id, cancellationToken); AnsiConsole.WriteLine("Device access revoked."); }
                else AnsiConsole.WriteLine("Use revoke <device-id>.");
            }
            await Task.Delay(100, cancellationToken);
        }
    }

    private static void ShowQr(PairedAccess? access, bool qr)
    {
        if (Client?.TunnelUrl is null || (access is null && !qr)) return;
        var url = access is null ? Client.TunnelUrl : ((HttpTunnelClient)Client).CreatePairingUrl();
        if (url is null) return;
        Console.WriteLine((access is null ? "Public URL: " : "Pairing URL: ") + url);
        if (access is not null) AnsiConsole.WriteLine("This code expires in five minutes and can pair one device. Press p for a new code.");
        if (!Console.IsOutputRedirected)
        {
            using var data = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.M);
            using var code = new AsciiQRCode(data);
            var graphic = AnsiConsole.Profile.Capabilities.Unicode
                ? code.GetGraphicSmall(invert: true)
                : code.GetGraphic(1, darkColorString: "##", whiteSpaceString: "  ");
            var width = graphic.Split('\n')[0].Length;
            if (width > AnsiConsole.Profile.Width)
            {
                Console.WriteLine($"Widen the terminal to {width} columns to display the QR code, or open the URL above.");
                return;
            }
            if (AnsiConsole.Profile.Capabilities.Ansi)
                AnsiConsole.Write(new Text(graphic + Environment.NewLine, new Style(Color.Black, Color.White)));
            else
            {
                var foreground = Console.ForegroundColor;
                var background = Console.BackgroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.Black;
                    Console.BackgroundColor = ConsoleColor.White;
                    Console.WriteLine(graphic);
                }
                finally { Console.ForegroundColor = foreground; Console.BackgroundColor = background; }
            }
        }
    }

    private static Table WriteStatusTable(string localUrl, string? tunnelUrl, string color, string currentStatus)
    {
        ClearScreen();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn(new Markup($"[{color}]{currentStatus}[/]")).Centered())
            .AddRow(Markup.Escape($"Local URL: {localUrl}"))
            .AddRow(Markup.Escape($"Public URL: {tunnelUrl}"));

        AnsiConsole.Write(table);

        WriteHelp();

        return table;
    }

    private static void ClearScreen()
    {
        // Console.Clear() throws when output is redirected (CI, a parent process, a log file).
        if (!Console.IsOutputRedirected)
        {
            AnsiConsole.Clear();
        }
    }

    private static void WriteHelp()
    {
        AnsiConsole.MarkupLine("\n[grey]c: clear · q: quit · p: new pairing QR · d: devices · r: revoke[/]");
        AnsiConsole.WriteLine();
    }

    private static void Log(string log)
    {
        string entry = $"[yellow] {DateTimeOffset.Now:HH:mm:ss}[/]: {Markup.Escape(log)}";
        AnsiConsole.MarkupLine(entry);
    }

    private static void LogRequest(string method, string path)
    {
        string entry = $"[green] {DateTimeOffset.Now:HH:mm:ss} [[{method}]][/]: {Markup.Escape(path)}";
        AnsiConsole.MarkupLine(entry);
    }

    private static void LogFailedRequest(string method, string path)
    {
        string entry = $"[red] {DateTimeOffset.Now:HH:mm:ss} [[{method}]][/]: {Markup.Escape(path)}";
        AnsiConsole.MarkupLine(entry);
    }

    private static void LogError(string message)
    {
        string entry = $"[red] {DateTimeOffset.Now:HH:mm:ss}[/]: {Markup.Escape(message)}";
        AnsiConsole.MarkupLine(entry);
    }

    private static void LogException(Exception ex)
    {
        AnsiConsole.WriteException(ex);
    }

    private static void AddLogging(ITunnelClient client)
    {
        client.Log += Log;
        client.LogRequest += LogRequest;
        client.LogFailedRequest += LogFailedRequest;
        client.LogError += LogError;
        client.LogException += LogException;
    }
}
