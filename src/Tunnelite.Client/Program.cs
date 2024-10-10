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

    public static async Task Main(string[] args)
    {
        var localUrlArgument = new Argument<string>("localUrl", "The local URL to tunnel to.");
        var logLevelOption = new Option<LogLevel?>(
            "--log",
            () => null,
            "The logging level (e.g., Trace, Debug, Information, Warning, Error, Critical)");
        var publicUrlOption = new Option<string>(
            "--publicUrl",
            () => "https://tunnelite.com",
            "The public server URL.");

        var rootCommand = new RootCommand
        {
            localUrlArgument,
            publicUrlOption,
            logLevelOption
        };
        rootCommand.Description = "CLI tool to create a tunnel to a local server.";

        rootCommand.SetHandler(async (string localUrl, string publicUrl, LogLevel? logLevel) =>
        {
            if (string.IsNullOrWhiteSpace(localUrl))
            {
                AnsiConsole.MarkupLine("[red]Error: Local URL is required.[/]");
                return;
            }

            Uri uri;
            try
            {
                uri = new Uri(localUrl);
            }
            catch (UriFormatException)
            {
                AnsiConsole.MarkupLine("[red]Error: Invalid URL format.[/]");
                return;
            }

            var scheme = uri.Scheme.ToLowerInvariant();
            publicUrl = publicUrl.TrimEnd('/');

            await AnsiConsole.Status()
                .StartAsync("Initializing tunnel...", async ctx =>
                {
                    ctx.Spinner(Spinner.Known.Dots);
                    ctx.SpinnerStyle(Style.Parse("green"));

                    switch (scheme)
                    {
                        case "tcp":

                            await InitializeTcpTunnel(localUrl, publicUrl, uri, logLevel, ctx);
                            break;

                        case "http":
                        case "https":

                            await InitializeHttpTunnel(localUrl, publicUrl, logLevel, ctx);
                            break;

                        default:
                            AnsiConsole.MarkupLine("[red]Error: Unsupported protocol. Use tcp:// or http(s)://[/]");
                            return;
                    }
                });

            await RunMainLoop(localUrl);

        }, localUrlArgument, publicUrlOption, logLevelOption);

        await rootCommand.InvokeAsync(args);
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

    private static async Task InitializeHttpTunnel(string localUrl, string publicUrl, LogLevel? logLevel, StatusContext ctx)
    {
        var httpTunnel = new HttpTunnelRequest
        {
            ClientId = ClientId,
            LocalUrl = localUrl,
            PublicUrl = publicUrl,
        };

        ctx.Status("Connecting to HTTP tunnel...");

        Client = new HttpTunnelClient(httpTunnel, logLevel);

        AddLogging(Client);

        await Client.ConnectAsync();

        ctx.Status("HTTP tunnel established.");
    }

    private static async Task RunMainLoop(string localUrl)
    {
        if (Client == null)
        {
            return;
        }

        Table statusTable = WriteStatusTable(localUrl, Client.TunnelUrl, "green", "Connected");

        Client.Connected += () =>
        {
            statusTable = WriteStatusTable(localUrl, Client.TunnelUrl, "green", "Connected");
            return Task.CompletedTask;
        };

        Client.Connection.Closed += (error) =>
        {
            statusTable = WriteStatusTable(localUrl, Client.TunnelUrl, "red", "Disconnected");
            return Task.CompletedTask;
        };

        Client.Connection.Reconnecting += (error) =>
        {
            statusTable = WriteStatusTable(localUrl, Client.TunnelUrl, "yellow", "Reconnecting");
            return Task.CompletedTask;
        };

        Client.Connection.Reconnected += (connectionId) =>
        {
            statusTable = WriteStatusTable(localUrl, Client.TunnelUrl, "green", "Connected");
            return Task.CompletedTask;
        };

        while (true)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                switch (key.KeyChar)
                {
                    case 'c':
                        AnsiConsole.Clear();
                        AnsiConsole.Write(statusTable);
                        WriteHelp();
                        break;
                    case 'q':
                        return;
                }
            }

            await Task.Delay(100);
        }
    }

    private static Table WriteStatusTable(string localUrl, string? tunnelUrl, string color, string currentStatus)
    {
        AnsiConsole.Clear();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn(new Markup($"[{color}]{currentStatus}[/]")).Centered())
            .AddRow($"Local URL: {localUrl}")
            .AddRow($"Public URL: {tunnelUrl}");

        AnsiConsole.Write(table);

        WriteHelp();

        return table;
    }

    private static void WriteHelp()
    {
        AnsiConsole.MarkupLine("\n[grey]Press 'c' to clear, 'q' to quit[/]");
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