using Spectre.Console;
using Spectre.Console.Rendering;
using Tunnelite.Sdk;

namespace Tunnelite.Client;

internal enum DevicePanel { Closed, List, Revoke }
internal sealed record CliView(string Status, string? PublicUrl = null, TerminalQr? Qr = null,
    string Notice = "", DevicePanel Panel = DevicePanel.Closed, int SelectedDevice = 0,
    IReadOnlyList<PairedDevice>? Devices = null, DateTimeOffset? QrExpires = null, string? PairingUrl = null)
{
    internal CliView WithDevices(IReadOnlyList<PairedDevice> devices)
    {
        if (ReferenceEquals(Devices, devices)) return this;
        var selected = SelectedDevice >= 0 && SelectedDevice < (Devices?.Count ?? 0) ? Devices![SelectedDevice].Id : (Guid?)null;
        // Enrollment can remove expired records. Keep selection attached to its device, never its old row number.
        var index = selected is null ? SelectedDevice : devices.Select((device, index) => (device, index))
            .Where(item => item.device.Id == selected).Select(item => item.index).DefaultIfEmpty(-1).First();
        return this with { Devices = devices, SelectedDevice = index };
    }
}

/// <summary>Only the command loop changes the view. Transport callbacks publish text without writing to the console.</summary>
internal sealed class CliDisplay(IAnsiConsole console, string localUrl, bool paired, bool live, TextWriter? plainOutput = null)
{
    private readonly TextWriter _output = plainOutput ?? Console.Out;
    private readonly bool _outputRedirected = plainOutput is not null || Console.IsOutputRedirected;
    private CliView _view = new("Connecting");
    internal CliActivity Activity { get; } = new();
    private long _printedRequests, _printedFailures, _printedError, _viewRevision;
    private long _lastPlainUpdate;
    private int _animationFrame;

    internal bool Live { get; } = live;
    internal CliView View => Volatile.Read(ref _view);
    internal void SetView(CliView view)
    {
        if (View == view) return;
        Volatile.Write(ref _view, view);
        Interlocked.Increment(ref _viewRevision);
    }
    internal void Request(string method, string path) => Activity.Request(method, path);
    internal void Error(string message) => Activity.Error(message);
    internal void ClearActivity() => Activity.Clear();

    internal void Notice(string message)
    {
        SetView(View with { Notice = TerminalText.Clean(message) });
        if (!Live) _output.WriteLine(TerminalText.Clean(message));
    }

    internal void Ready(string url, string? qrUrl)
    {
        TerminalQr? qr = null;
        if (qrUrl is not null && (Live || !_outputRedirected))
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
            if (!_outputRedirected && console.Profile.Capabilities.Ansi)
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
            else
            {
                _output.WriteLine(qr.Graphic);
            }
        }
        _output.WriteLine(Help());
    }

    internal void ShowDevices(DevicePanel panel, IReadOnlyList<PairedDevice> devices)
    {
        var updated = View.WithDevices(devices);
        var selected = View.Panel != DevicePanel.Closed && updated.SelectedDevice >= 0 && updated.SelectedDevice < devices.Count
            ? updated.SelectedDevice
            : 0;
        SetView(updated with { Panel = panel, SelectedDevice = selected, Notice = "" });
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
            try
            {
                console.Clear();
                while (!running.IsCompleted)
                {
                    var width = console.Profile.Width;
                    var height = console.Profile.Height;
                    await console.Live(Render()).AutoClear(true).Overflow(VerticalOverflow.Crop).StartAsync(async context =>
                    {
                        (long, long, long)? rendered = null;
                        while (!running.IsCompleted && width == console.Profile.Width && height == console.Profile.Height)
                        {
                            var revision = (Volatile.Read(ref _viewRevision), Activity.Revision, NextTimeTick());
                            if (rendered != revision)
                            {
                                context.UpdateTarget(Render());
                                context.Refresh();
                                rendered = revision;
                            }
                            await Task.WhenAny(running, Task.Delay(200, lifetime.Token));
                            lifetime.Token.ThrowIfCancellationRequested();
                        }
                    });
                    if (!running.IsCompleted) console.Clear();
                }
            }
            finally
            {
                _output.Write("\u001b[?1049l");
                _output.Flush();
            }
            await running;
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await running; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
        _output.WriteLine("Tunnel closed.");
    }

    private long NextTimeTick()
    {
        var view = View;
        if (view.Status != "Connected")
        {
            return Interlocked.Increment(ref _animationFrame);
        }
        Volatile.Write(ref _animationFrame, 0);
        if (view.QrExpires is { } expires && DateTimeOffset.UtcNow <= expires.AddSeconds(1))
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        return 0;
    }

    internal void RefreshPlain(bool force = false)
    {
        if (Live || (!force && Environment.TickCount64 - _lastPlainUpdate < 1000)) return;
        _lastPlainUpdate = Environment.TickCount64;
        var activity = Activity.Snapshot();
        if (activity.Requests != _printedRequests || activity.Failures != _printedFailures)
        {
            _output.WriteLine($"Requests: {activity.Requests} | Proxy errors: {activity.Failures} | Latest: {activity.Latest}");
            _printedRequests = activity.Requests;
            _printedFailures = activity.Failures;
        }
        foreach (var error in activity.Errors.Where(e => e.Sequence > _printedError))
        {
            _output.WriteLine("Error: " + error.Message);
            _printedError = Math.Max(_printedError, error.Sequence);
        }
    }

    internal IRenderable Render() =>
        CliDashboardRenderer.Render(console, localUrl, paired, View, Activity.Snapshot(), Volatile.Read(ref _animationFrame));

    private string Help() => View.Panel switch
    {
        DevicePanel.Revoke => "Up/Down Select  Enter Revoke  Esc Cancel  q Quit",
        DevicePanel.List => "Up/Down Scroll  Esc Back  r Revoke  q Quit",
        _ => paired ? "q Quit  p New QR  d Devices  r Revoke  c Clear activity" : "q Quit  c Clear activity",
    };
}
