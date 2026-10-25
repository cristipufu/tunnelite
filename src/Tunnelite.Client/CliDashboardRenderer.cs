using Spectre.Console;
using Spectre.Console.Rendering;

namespace Tunnelite.Client;

internal static class CliDashboardRenderer
{
    private const int MaxWidth = 112;
    private static readonly Style Accent = new(new Color(56, 189, 248));
    private static readonly Style Strong = new(decoration: Decoration.Bold);
    private static readonly Style Border = new(new Color(91, 111, 139));
    private static readonly Style Key = new(decoration: Decoration.Bold | Decoration.Invert);
    private static readonly Style QrInk = new(new Color(0, 0, 0), new Color(255, 255, 255));

    internal static IRenderable Render(IAnsiConsole console, string localUrl, bool paired, CliView view, ActivitySnapshot activity)
    {
        var screenWidth = console.Profile.Width;
        var screenHeight = console.Profile.Height;
        var margin = screenWidth >= 100 ? 4 : screenWidth >= 60 ? 2 : screenWidth >= 30 ? 1 : 0;
        var usableWidth = Math.Max(1, screenWidth - margin * 2);
        var width = Math.Min(MaxWidth, usableWidth);
        var top = screenHeight >= 32 ? 2 : screenHeight >= 18 ? 1 : 0;
        var availableHeight = Math.Max(1, screenHeight - top - 1);
        var options = new RenderOptions(console.Profile.Capabilities, new Size(width, availableHeight));
        int Height(IRenderable item, int itemWidth) => item.Render(options, itemWidth).Count(segment => segment.IsLineBreak) + 1;

        IRenderable Frame(bool spacious, bool showQr)
        {
            var innerWidth = Math.Max(1, width - (spacious ? 6 : 0));
            IRenderable body;
            if (view.Panel != DevicePanel.Closed) body = Devices(view, innerWidth, availableHeight);
            else
            {
                var qr = showQr ? view.Qr : null;
                var gap = spacious ? 4 : 2;
                var minDetails = spacious ? 40 : 28;
                if (qr is not null && qr.Width + gap + minDetails <= innerWidth)
                {
                    var qrContent = Qr(qr, paired, spacious);
                    var detailsWidth = innerWidth - qr.Width - gap;
                    var details = Details(localUrl, paired, view, activity, detailsWidth, spacious,
                        screenHeight < 20, Height(qrContent, qr.Width), Height);
                    var columns = new Grid();
                    columns.AddColumn(new GridColumn { Width = detailsWidth + gap, Padding = new Padding(0, 0, gap, 0) });
                    columns.AddColumn(new GridColumn { Width = qr.Width, Padding = new Padding(0) });
                    columns.AddRow(details, qrContent);
                    body = columns;
                }
                else
                {
                    var details = Details(localUrl, paired, view, activity, innerWidth, spacious, screenHeight < 20, 0, Height);
                    body = qr is not null ? new Rows(details, Text.Empty, Qr(qr, paired, spacious)) : details;
                }
                if (!showQr && view.Qr is not null && (view.QrExpires is null || view.QrExpires > DateTimeOffset.UtcNow))
                    body = new Rows(body, new Text("Resize the terminal to show the QR; the link above also works.").Ellipsis());
            }

            IRenderable card = spacious ? new Panel(body)
            {
                Border = console.Profile.Capabilities.Unicode ? BoxBorder.Rounded : BoxBorder.Ascii,
                BorderStyle = Border, Padding = new Padding(2, 1), Width = width, Expand = true,
                Header = new PanelHeader(view.Panel switch
                {
                    DevicePanel.List => " PAIRED DEVICES ",
                    DevicePanel.Revoke => " REVOKE A DEVICE ",
                    _ => " CONNECTION ",
                }),
            } : body;
            var header = Header(view, width, spacious, paired, console.Profile.Capabilities.Unicode);
            var footer = new Padder(Legend(view.Panel, paired, width, console.Profile.Capabilities.Unicode), new Padding(spacious ? 2 : 0, 0));
            return screenHeight < 26
                ? new Rows(header, card, footer)
                : new Rows(header, Text.Empty, card, Text.Empty, footer);
        }

        var qrAvailable = view.Status == "Connected" && view.Panel == DevicePanel.Closed && view.Qr is { } candidate
            && candidate.Width <= width && (view.QrExpires is null || view.QrExpires > DateTimeOffset.UtcNow);
        var spacious = screenHeight >= 30 && width >= 64;
        if (qrAvailable && spacious && view.Qr!.Width > width - 6) spacious = false;
        var frame = Frame(spacious, qrAvailable);
        if (Height(frame, width) > availableHeight && spacious)
        {
            spacious = false;
            frame = Frame(false, qrAvailable);
        }
        if (qrAvailable && Height(frame, width) > availableHeight) frame = Frame(spacious, false);
        // Center the surface as a whole, preserving left alignment within it.
        var left = Math.Max(margin, (screenWidth - width) / 2);
        return new Padder(new FixedWidth(frame, width), new Padding(left, top, screenWidth - width - left, 0));
    }

    private sealed class FixedWidth(IRenderable content, int width) : IRenderable
    {
        public Measurement Measure(RenderOptions options, int maxWidth) => new(Math.Min(width, maxWidth), Math.Min(width, maxWidth));
        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => content.Render(options, Math.Min(width, maxWidth));
    }

    private static IRenderable Header(CliView view, int width, bool spacious, bool paired, bool unicode)
    {
        if (width < 24) return new Text(view.Status, Strong).Ellipsis();
        var statusWidth = Math.Min(16, Math.Max(1, width / 2));
        var header = new Grid { Width = width };
        header.AddColumn(new GridColumn { Width = width - statusWidth, Padding = new Padding(0) });
        header.AddColumn(new GridColumn { Width = statusWidth, Padding = new Padding(0), Alignment = Justify.Right });
        var brand = new Paragraph().Append(unicode ? "◈  " : "+  ", Accent).Append("tunnelite", Strong);
        if (spacious) brand.Append(paired ? "  /  paired access" : "  /  public tunnel");
        header.AddRow(brand, new Text((unicode ? "● " : "* ") + view.Status,
            new Style(view.Status == "Connected" ? Color.Green : Color.Yellow, decoration: Decoration.Bold)));
        return header;
    }

    private static IRenderable Details(string localUrl, bool paired, CliView view, ActivitySnapshot activity,
        int width, bool spacious, bool minimal, int targetHeight, Func<IRenderable, int, int> height)
    {
        var connection = new List<IRenderable>();
        if (!minimal) connection.Add(new Text("OPEN IN YOUR BROWSER", Strong));
        connection.Add(Address(view.PublicUrl ?? "Waiting for the relay...", primary: true));
        connection.Add(new Text(paired ? "Paired browsers only" : "Public link · anyone with the URL").Ellipsis());
        if (spacious) connection.Add(Text.Empty);
        if (!minimal) connection.Add(new Text("FORWARDS TO", Strong));
        connection.Add(Address(localUrl));
        if (paired && view.Status == "Connected" && view.PairingUrl is not null)
        {
            if (spacious) connection.Add(Text.Empty);
            connection.Add(new Text("PAIRING LINK", Strong));
            connection.Add(Address(view.PairingUrl));
        }
        if (paired && view.Status == "Connected" && view.QrExpires is { } expires)
        {
            var remaining = expires - DateTimeOffset.UtcNow;
            connection.Add(new Text(remaining > TimeSpan.Zero ? $"One browser · code expires in {remaining:mm\\:ss}"
                : "Pairing code expired · p for a new QR").Ellipsis());
        }

        var traffic = new List<IRenderable>();
        if (spacious)
        {
            var stats = new Grid();
            var firstWidth = Math.Max(1, width / 2);
            stats.AddColumn(new GridColumn { Width = firstWidth, Padding = new Padding(0) });
            stats.AddColumn(new GridColumn { Width = Math.Max(1, width - firstWidth), Padding = new Padding(0) });
            stats.AddRow(new Text($"{activity.Requests:N0}", Strong),
                new Text($"{activity.Failures:N0}", new Style(activity.Failures > 0 ? Color.Red : Color.Default, decoration: Decoration.Bold)));
            stats.AddRow(new Text("requests"), new Text("proxy errors"));
            traffic.Add(stats);
            traffic.Add(Text.Empty);
        }
        else traffic.Add(new Text($"Requests {activity.Requests}   Proxy errors {activity.Failures}"));
        if (!minimal) traffic.Add(new Text("LATEST REQUEST", Strong));
        traffic.Add(new Text(activity.Latest).Ellipsis());
        if (activity.Errors.Length > 0 && spacious) traffic.Add(new Text("RECENT ERRORS", new Style(Color.Red)));
        foreach (var error in activity.Errors.TakeLast(spacious ? CliActivity.RecentErrorLimit : 1))
            traffic.Add(new Text(error.Message, new Style(Color.Red)).Ellipsis());
        if (view.Notice.Length > 0) traffic.Add(new Text(view.Notice).Ellipsis());

        var top = new Rows(connection);
        var bottom = new Rows(traffic);
        var spacer = Math.Max(spacious ? 2 : 1, targetHeight - height(top, width) - height(bottom, width));
        return new Rows(new[] { top }.Cast<IRenderable>().Concat(Enumerable.Repeat<IRenderable>(Text.Empty, spacer)).Append(bottom));
    }

    private static IRenderable Address(string value, bool primary = false)
    {
        var clean = TerminalText.Clean(value, 2048);
        var link = Uri.TryCreate(clean, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? clean : null;
        return new Text(clean, new Style(primary ? Accent.Foreground : null,
            decoration: primary ? Decoration.Bold : Decoration.None, link: link)).Fold();
    }

    private static IRenderable Qr(TerminalQr qr, bool paired, bool spacious)
    {
        var rows = new List<IRenderable>();
        if (spacious)
        {
            rows.Add(Align.Center(new Text(paired ? "PAIR YOUR BROWSER" : "OPEN ON YOUR PHONE", Strong)));
            rows.Add(Text.Empty);
        }
        rows.Add(new Text(qr.Graphic, QrInk));
        if (spacious) rows.Add(Text.Empty);
        rows.Add(Align.Center(new Text(paired ? "Scan to pair this browser" : "Scan to open on your phone")));
        return new Rows(rows);
    }

    private static IRenderable Devices(CliView view, int width, int availableHeight)
    {
        var devices = view.Devices ?? [];
        var rows = new List<IRenderable>();
        if (view.Panel == DevicePanel.Revoke) rows.Add(new Text("Choose the browser whose access you want to revoke."));
        if (devices.Count == 0) rows.Add(new Text("No paired devices yet."));
        rows.Add(Text.Empty);
        var count = Math.Min(6, Math.Max(1, (availableHeight - 12) / 3));
        var start = Math.Max(0, view.SelectedDevice - count / 2);
        foreach (var (device, index) in devices.Select((device, index) => (device, index)).Skip(start).Take(count))
        {
            var selected = index == view.SelectedDevice;
            rows.Add(new Text((selected ? "> " : "  ") + TerminalText.Clean(device.Name), selected ? Strong : Style.Plain).Ellipsis());
            rows.Add(new Text(width >= 62 ? $"  {device.Id}  ·  expires {device.ExpiresAt:yyyy-MM-dd}"
                : $"  {device.Id}").Ellipsis());
            rows.Add(Text.Empty);
        }
        if (devices.Count > count) rows.Add(new Text($"{Math.Max(0, view.SelectedDevice + 1)} of {devices.Count} · Up/Down to scroll"));
        if (view.Notice.Length > 0) rows.Add(new Text(view.Notice).Ellipsis());
        return new Rows(rows);
    }

    private static IRenderable Legend(DevicePanel panel, bool paired, int width, bool unicode)
    {
        var arrows = unicode ? "↑↓" : "up/down";
        var keys = panel switch
        {
            DevicePanel.Revoke => new[] { (arrows, "Select"), ("enter", "Revoke"), ("esc", "Cancel"), ("q", "Quit") },
            DevicePanel.List => new[] { (arrows, "Scroll"), ("esc", "Back"), ("r", "Revoke"), ("q", "Quit") },
            _ when paired => new[] { ("q", "Quit"), ("p", "New QR"), ("d", "Devices"), ("r", "Revoke"), ("c", "Clear") },
            _ => new[] { ("q", "Quit"), ("c", width >= 60 ? "Clear activity" : "Clear") },
        };
        var legend = new Paragraph();
        foreach (var (key, label) in keys)
            legend.Append(" " + key + " ", Key).Append(" " + label + "   ");
        return legend;
    }
}
