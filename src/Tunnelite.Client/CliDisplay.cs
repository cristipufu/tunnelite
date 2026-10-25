using Spectre.Console;
using Spectre.Console.Rendering;
using Tunnelite.Sdk;

namespace Tunnelite.Client;

internal enum DevicePanel { Closed, List, Revoke }
internal sealed record CliView(string Status, string? PublicUrl = null, TerminalQr? Qr = null,
    string Notice = "", DevicePanel Panel = DevicePanel.Closed, int SelectedDevice = 0,
    IReadOnlyList<PairedDevice>? Devices = null, DateTimeOffset? QrExpires = null, string? PairingUrl = null);

/// <summary>Only the command loop changes the view. Transport callbacks publish text without writing to the console.</summary>
internal sealed class CliDisplay(IAnsiConsole console, string localUrl, bool paired, bool live)
{
    private readonly TextWriter _output = Console.Out;
    private static readonly Style Muted = new(Color.Grey);
    private static readonly Style Accent = new(Color.DeepSkyBlue1);
    private CliView _view = new("Connecting");
    private string _latest = "Waiting for requests";
    private string _error = "";
    private string _printedError = "";
    private string _printedLatest = "";
    private long _lastPlainUpdate;

    internal bool Live { get; } = live;
    internal CliView View => Volatile.Read(ref _view);
    internal void SetView(CliView view) => Volatile.Write(ref _view, view);
    internal void Request(string method, string path) => Volatile.Write(ref _latest, TerminalText.Request(method, path));
    internal void Error(string message) => Volatile.Write(ref _error, TerminalText.Clean(message, 240));
    internal void ClearActivity() { Volatile.Write(ref _latest, "Waiting for requests"); Volatile.Write(ref _error, ""); }

    internal void Notice(string message)
    {
        SetView(View with { Notice = TerminalText.Clean(message) });
        if (!Live) _output.WriteLine(TerminalText.Clean(message));
    }

    internal void Ready(string url, string? qrUrl)
    {
        TerminalQr? qr = null;
        if (qrUrl is not null && !Console.IsOutputRedirected)
        {
            try { qr = TerminalQr.Create(qrUrl, console.Profile.Capabilities.Unicode); }
            catch (Exception exception) when (exception is IOException or PlatformNotSupportedException or InvalidOperationException)
            { Error("This terminal cannot render the QR code. Open the URL instead."); }
        }
        SetView(View with { Status = "Connected", PublicUrl = url, Qr = qr, Notice = "",
            QrExpires = paired && qrUrl is not null ? DateTimeOffset.UtcNow.AddMinutes(5) : null,
            PairingUrl = paired ? qrUrl : null });
        if (Live) return;
        _output.WriteLine("Local URL: " + TerminalText.Clean(localUrl));
        _output.WriteLine("Public URL: " + TerminalText.Clean(url));
        if (paired && qrUrl is not null) _output.WriteLine("Pairing URL: " + TerminalText.Clean(qrUrl, 2048));
        _output.WriteLine(paired ? "Access: Paired browsers" : "Access: Public link");
        if (qr is not null && qr.Width <= console.Profile.Width)
        {
            var foreground = Console.ForegroundColor;
            var background = Console.BackgroundColor;
            try
            {
                Console.ForegroundColor = ConsoleColor.Black;
                Console.BackgroundColor = ConsoleColor.White;
                _output.WriteLine(qr.Graphic);
            }
            finally
            {
                Console.ResetColor();
                if ((uint)foreground <= 15) Console.ForegroundColor = foreground;
                if ((uint)background <= 15) Console.BackgroundColor = background;
            }
        }
        _output.WriteLine(Help());
    }

    internal void ShowDevices(DevicePanel panel, IReadOnlyList<PairedDevice> devices)
    {
        SetView(View with { Panel = panel, Devices = devices, SelectedDevice = 0, Notice = "" });
        if (Live) return;
        if (devices.Count == 0) _output.WriteLine("No paired devices yet.");
        foreach (var device in devices)
            _output.WriteLine($"{device.Id}  {TerminalText.Clean(device.Name)}  expires {device.ExpiresAt:u}");
        if (panel == DevicePanel.Revoke) _output.WriteLine("Up/Down: select | Enter: revoke | Esc: cancel");
    }

    internal async Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!Live)
        {
            _output.WriteLine("Tunnelite | Connecting...");
            try { await work(lifetime.Token); }
            finally { RefreshPlain(force: true); }
            return;
        }
        var running = work(lifetime.Token);
        // A separate screen keeps request refreshes and terminal resizing out of shell scrollback.
        _output.Write("\u001b[?1049h");
        try
        {
            while (!running.IsCompleted)
            {
                var width = console.Profile.Width;
                var height = console.Profile.Height;
                await console.Live(Render()).AutoClear(true).Overflow(VerticalOverflow.Crop).StartAsync(async context =>
                {
                    while (!running.IsCompleted && width == console.Profile.Width && height == console.Profile.Height)
                    {
                        context.UpdateTarget(Render());
                        context.Refresh();
                        await Task.WhenAny(running, Task.Delay(200, lifetime.Token));
                        lifetime.Token.ThrowIfCancellationRequested();
                    }
                });
                if (!running.IsCompleted) console.Clear();
            }
            await running;
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await running; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            finally { _output.Write("\u001b[?1049l"); }
        }
        _output.WriteLine("Tunnel closed.");
    }

    internal void RefreshPlain(bool force = false)
    {
        if (Live || (!force && Environment.TickCount64 - _lastPlainUpdate < 1000)) return;
        _lastPlainUpdate = Environment.TickCount64;
        var latest = Volatile.Read(ref _latest);
        var error = Volatile.Read(ref _error);
        if (latest != _printedLatest && latest != "Waiting for requests")
        { _output.WriteLine("Latest request: " + latest); _printedLatest = latest; }
        if (error != _printedError && error.Length > 0)
        { _output.WriteLine("Error: " + error); _printedError = error; }
    }

    internal IRenderable Render()
    {
        var view = View;
        var content = Details(view);
        IRenderable body = content;
        if (view.Panel != DevicePanel.Closed) body = DeviceList(view);
        else if (view.Status == "Connected" && view.Qr is { } qr && (view.QrExpires is null || view.QrExpires > DateTimeOffset.UtcNow))
        {
            var qrContent = new Rows(new Text(qr.Graphic, new Style(Color.Black, Color.White)),
                new Text(paired ? "Scan to pair this browser" : "Scan to open on your phone", Muted));
            if (console.Profile.Width >= qr.Width + 38)
            {
                var columns = new Grid();
                columns.AddColumn(new GridColumn { Width = console.Profile.Width - qr.Width - 4, Padding = new Padding(0, 0, 3, 0) });
                columns.AddColumn(new GridColumn { Width = qr.Width, Padding = new Padding(0) });
                columns.AddRow(content, qrContent);
                body = columns;
            }
            else body = new Rows(content, Text.Empty, qrContent);

            // Never crop the QR itself: a partial code looks valid but cannot be scanned.
            var options = new RenderOptions(console.Profile.Capabilities, new Size(console.Profile.Width, console.Profile.Height));
            // Measure directly: the console pipeline contains the active live-display hook.
            if (qr.Width > console.Profile.Width || Frame(body).Render(options, console.Profile.Width).Count(s => s.IsLineBreak) >= console.Profile.Height)
                body = new Rows(content, new Text("Resize the terminal to show the QR, or open the URL above.", Muted));
        }
        return Frame(body);
    }

    private IRenderable Frame(IRenderable body) => new Rows(
        new Text("TUNNELITE", new Style(Color.DeepSkyBlue1, decoration: Decoration.Bold)),
        Text.Empty, body, Text.Empty, new Text(Help(), Muted));

    private IRenderable Details(CliView view)
    {
        var compact = console.Profile.Height < 24;
        var statusColor = view.Status == "Connected" ? Color.Green : Color.Yellow;
        var rows = new List<IRenderable>
        {
            new Text((console.Profile.Capabilities.Unicode ? "● " : "* ") + view.Status, new Style(statusColor)),
        };
        if (!compact) rows.Add(Text.Empty);
        void Address(string label, string value)
        {
            if (!compact) rows.Add(new Text(label, Muted));
            rows.Add(new Text(TerminalText.Clean(value, 2048)).Fold());
        }
        Address("LOCAL", localUrl);
        Address("PUBLIC", view.PublicUrl ?? "Waiting for the relay...");
        rows.Add(new Text(paired ? "Paired browsers only" : "Public link · anyone with the URL", Muted));
        if (paired && view.Status == "Connected" && view.PairingUrl is not null) Address("PAIRING LINK", view.PairingUrl);
        if (!compact) rows.Add(Text.Empty);
        rows.Add(new Text("ACTIVITY", Accent));
        rows.Add(new Text(Volatile.Read(ref _latest)).Ellipsis());
        var error = Volatile.Read(ref _error);
        if (error.Length > 0) rows.Add(new Text(error, new Style(Color.Red)).Ellipsis());
        if (view.Notice.Length > 0) rows.Add(new Text(view.Notice, Muted).Ellipsis());
        if (paired && view.Status == "Connected" && view.QrExpires is { } expires)
        {
            var remaining = expires - DateTimeOffset.UtcNow;
            rows.Add(new Text(remaining > TimeSpan.Zero ? $"Pairing code: one browser · expires in {remaining:mm\\:ss}"
                : "Pairing code expired · press p for a new code", Muted));
        }
        return new Rows(rows);
    }

    private IRenderable DeviceList(CliView view)
    {
        var devices = view.Devices ?? [];
        var rows = new List<IRenderable> { new Text(view.Panel == DevicePanel.Revoke ? "REVOKE A DEVICE" : "PAIRED DEVICES", Accent), Text.Empty };
        if (devices.Count == 0) rows.Add(new Text("No paired devices yet.", Muted));
        var start = Math.Max(0, view.SelectedDevice - 3);
        var count = Math.Min(6, Math.Max(1, (console.Profile.Height - 10) / 2));
        foreach (var (device, index) in devices.Select((device, index) => (device, index)).Skip(start).Take(count))
        {
            var selected = view.Panel == DevicePanel.Revoke && index == view.SelectedDevice;
            rows.Add(new Text((selected ? "> " : "  ") + TerminalText.Clean(device.Name), selected ? Accent : Style.Plain));
            rows.Add(new Text($"  {device.Id} · expires {device.ExpiresAt:yyyy-MM-dd}", Muted));
        }
        if (devices.Count > count) rows.Add(new Text($"{view.SelectedDevice + 1} of {devices.Count} · Up/Down to scroll", Muted));
        if (view.Notice.Length > 0) rows.Add(new Text(view.Notice, Muted));
        return new Rows(rows);
    }

    private string Help() => View.Panel == DevicePanel.Revoke ? "Up/Down Select  Enter Revoke  Esc Cancel  q Quit"
        : paired ? "q Quit  p New QR  d Devices  r Revoke  c Clear activity" : "q Quit  c Clear activity";
}
