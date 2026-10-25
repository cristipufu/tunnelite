using Spectre.Console;
using Spectre.Console.Rendering;

namespace Tunnelite.Client;

internal static class CliTheme
{
    internal static readonly Style Accent = new(new Color(56, 189, 248));
    internal static readonly Style Strong = new(decoration: Decoration.Bold);
    internal static readonly Style Border = new(new Color(91, 111, 139));
    internal static readonly Style Key = new(decoration: Decoration.Bold | Decoration.Invert);
    internal static readonly Style QrInk = new(new Color(0, 0, 0), new Color(255, 255, 255));
    internal static readonly Style QrBorder = new(new Color(255, 255, 255));
    internal static readonly Style Error = new(Color.Red);

    private static readonly string[] UnicodeSpinner = ["⠋ ", "⠙ ", "⠹ ", "⠸ ", "⠼ ", "⠴ ", "⠦ ", "⠧ ", "⠇ ", "⠏ "];
    private static readonly string[] AsciiSpinner = ["- ", "\\ ", "| ", "/ "];

    internal static string BrandPrefix(bool unicode) => unicode ? "◈  " : "+  ";

    internal static string StatusPrefix(string status, bool unicode, int animationFrame)
    {
        if (status == "Connected" || animationFrame <= 0)
        {
            return unicode ? "● " : "* ";
        }

        var frames = unicode ? UnicodeSpinner : AsciiSpinner;
        return frames[animationFrame % frames.Length];
    }

    internal static Style StatusStyle(string status) =>
        new(status == "Connected" ? Color.Green : Color.Yellow, decoration: Decoration.Bold);

    internal static string PanelTitle(DevicePanel panel) => panel switch
    {
        DevicePanel.List => " PAIRED DEVICES ",
        DevicePanel.Revoke => " REVOKE A DEVICE ",
        _ => " CONNECTION ",
    };

    internal static (string Key, string Label)[] KeyBindings(DevicePanel panel, bool paired, int width, bool unicode)
    {
        var arrows = unicode ? "↑↓" : "up/down";
        return panel switch
        {
            DevicePanel.Revoke => [(arrows, "Select"), ("enter", "Revoke"), ("esc", "Cancel"), ("q", "Quit")],
            DevicePanel.List => [(arrows, "Scroll"), ("esc", "Back"), ("r", "Revoke"), ("q", "Quit")],
            _ when paired => [("q", "Quit"), ("p", "New QR"), ("d", "Devices"), ("r", "Revoke"), ("c", "Clear")],
            _ => [("q", "Quit"), ("c", width >= 60 ? "Clear activity" : "Clear")],
        };
    }
}

internal static class CliDashboardRenderer
{
    private const int MaxWidth = 112;

    private enum BodyDensity
    {
        Full,
        Compact,
        Minimal,
    }

    internal static IRenderable Render(
        IAnsiConsole console,
        string localUrl,
        bool paired,
        CliView view,
        ActivitySnapshot activity,
        int animationFrame = 0)
    {
        var screenWidth = console.Profile.Width;
        var screenHeight = console.Profile.Height;
        var margin = screenWidth >= 100 ? 4 : screenWidth >= 60 ? 2 : screenWidth >= 30 ? 1 : 0;
        var usableWidth = Math.Max(1, screenWidth - margin * 2);
        var width = Math.Min(MaxWidth, usableWidth);
        if (view.Qr is { } q && width < q.Width + 34 && screenWidth >= q.Width + 2)
        {
            margin = Math.Min(margin, Math.Max(0, (screenWidth - (q.Width + 34)) / 2));
            usableWidth = Math.Max(1, screenWidth - margin * 2);
            width = Math.Min(MaxWidth, usableWidth);
        }

        var top = screenHeight >= 32 ? 2 : screenHeight >= 18 ? 1 : 0;
        var availableHeight = Math.Max(1, screenHeight - top - 1);
        var options = new RenderOptions(console.Profile.Capabilities, new Size(width, availableHeight));
        int MeasureHeight(IRenderable item, int itemWidth)
        {
            var lines = 1;
            var endedWithBreak = false;
            foreach (var segment in item.Render(options, itemWidth))
            {
                if (segment.IsLineBreak)
                {
                    lines++;
                    endedWithBreak = true;
                }
                else
                {
                    endedWithBreak = false;
                }
            }

            return endedWithBreak ? Math.Max(1, lines - 1) : lines;
        }

        var panelChromeWidth = width >= 47 ? 4 : width >= 24 ? 2 : 0;
        var qrAvailable = view.Status == "Connected"
            && view.Panel == DevicePanel.Closed
            && view.Qr is { } candidate
            && candidate.Width <= width - panelChromeWidth
            && (view.QrExpires is null || view.QrExpires > DateTimeOffset.UtcNow);
        var spacious = screenHeight >= 30 && width >= 64;
        if (qrAvailable && spacious && view.Qr!.Width > width - 6)
        {
            spacious = false;
        }

        var frame = BuildFrame(console, localUrl, paired, view, activity, width, screenHeight, availableHeight, spacious, qrAvailable, BodyDensity.Full, mergeHeaderIntoPanel: false, animationFrame, MeasureHeight);
        var frameHeight = MeasureHeight(frame, width);
        if (frameHeight > availableHeight && spacious)
        {
            spacious = false;
            frame = BuildFrame(console, localUrl, paired, view, activity, width, screenHeight, availableHeight, spacious, qrAvailable, BodyDensity.Full, mergeHeaderIntoPanel: false, animationFrame, MeasureHeight);
            frameHeight = MeasureHeight(frame, width);
        }
        if (qrAvailable && frameHeight > availableHeight)
        {
            top = 0;
            availableHeight = screenHeight >= 24 ? screenHeight : Math.Max(1, screenHeight - 1);
            frame = BuildFrame(console, localUrl, paired, view, activity, width, screenHeight, availableHeight, spacious, qrAvailable, BodyDensity.Full, mergeHeaderIntoPanel: false, animationFrame, MeasureHeight);
            frameHeight = MeasureHeight(frame, width);
        }
        if (qrAvailable && screenHeight >= 20 && frameHeight > availableHeight)
        {
            frame = BuildFrame(console, localUrl, paired, view, activity, width, screenHeight, availableHeight, spacious, qrAvailable, BodyDensity.Compact, mergeHeaderIntoPanel: false, animationFrame, MeasureHeight);
            frameHeight = MeasureHeight(frame, width);
        }
        if (qrAvailable && screenHeight >= 20 && frameHeight > availableHeight)
        {
            frame = BuildFrame(console, localUrl, paired, view, activity, width, screenHeight, availableHeight, spacious, qrAvailable, BodyDensity.Minimal, mergeHeaderIntoPanel: false, animationFrame, MeasureHeight);
            frameHeight = MeasureHeight(frame, width);
        }
        if (qrAvailable && screenHeight >= 20 && frameHeight > availableHeight)
        {
            frame = BuildFrame(console, localUrl, paired, view, activity, width, screenHeight, availableHeight, spacious, qrAvailable, BodyDensity.Minimal, mergeHeaderIntoPanel: true, animationFrame, MeasureHeight);
            frameHeight = MeasureHeight(frame, width);
        }
        if (qrAvailable && frameHeight > availableHeight)
        {
            frame = BuildFrame(console, localUrl, paired, view, activity, width, screenHeight, availableHeight, spacious, showQr: false, BodyDensity.Full, mergeHeaderIntoPanel: false, animationFrame, MeasureHeight);
        }

        // Center the surface as a whole, preserving left alignment within it.
        var left = Math.Max(margin, (screenWidth - width) / 2);
        return new Padder(new FixedWidth(frame, width), new Padding(left, top, screenWidth - width - left, 0));
    }

    private static IRenderable BuildFrame(
        IAnsiConsole console,
        string localUrl,
        bool paired,
        CliView view,
        ActivitySnapshot activity,
        int width,
        int screenHeight,
        int availableHeight,
        bool spacious,
        bool showQr,
        BodyDensity density,
        bool mergeHeaderIntoPanel,
        int animationFrame,
        Func<IRenderable, int, int> measureHeight)
    {
        var unicode = console.Profile.Capabilities.Unicode;
        var showPanel = width >= 24 && screenHeight >= 14 && (showQr || spacious || view.Panel != DevicePanel.Closed || screenHeight >= 20);
        var padX = !showPanel ? 0 : spacious ? 2 : width >= 47 ? 1 : 0;
        var padY = spacious ? 1 : 0;
        var innerWidth = Math.Max(1, width - (showPanel ? 2 + padX * 2 : 0));
        var chromeLines = (showPanel ? 2 + padY * 2 : 0) + (mergeHeaderIntoPanel && showPanel ? 0 : 1) + 1;
        var body = view.Panel != DevicePanel.Closed
            ? Devices(view, innerWidth, availableHeight)
            : BuildConnectionBody(localUrl, paired, view, activity, innerWidth, availableHeight, chromeLines, screenHeight < 20, spacious, showQr, unicode, mergeHeaderIntoPanel, density, measureHeight);

        var panelTitle = mergeHeaderIntoPanel && showPanel
            ? $" {CliTheme.BrandPrefix(unicode)}tunnelite ·{CliTheme.PanelTitle(view.Panel)}· {CliTheme.StatusPrefix(view.Status, unicode, animationFrame)}{view.Status} "
            : CliTheme.PanelTitle(view.Panel);

        IRenderable card = showPanel ? new Panel(body)
        {
            Border = unicode ? BoxBorder.Rounded : BoxBorder.Ascii,
            BorderStyle = CliTheme.Border,
            Padding = new Padding(padX, padY),
            Width = width,
            Expand = true,
            Header = new PanelHeader(panelTitle),
        } : body;

        var footer = new Padder(Legend(view.Panel, paired, width, unicode), new Padding(spacious ? 2 : 0, 0));
        if (mergeHeaderIntoPanel && showPanel)
        {
            return new Rows(card, footer);
        }

        var header = Header(view, width, spacious, paired, unicode, animationFrame);
        return !spacious || screenHeight < 26
            ? new Rows(header, card, footer)
            : new Rows(header, Text.Empty, card, Text.Empty, footer);
    }

    private static IRenderable BuildConnectionBody(
        string localUrl,
        bool paired,
        CliView view,
        ActivitySnapshot activity,
        int innerWidth,
        int availableHeight,
        int chromeLines,
        bool minimal,
        bool spacious,
        bool showQr,
        bool unicode,
        bool mergeHeaderIntoPanel,
        BodyDensity density,
        Func<IRenderable, int, int> measureHeight)
    {
        var qr = showQr ? view.Qr : null;
        var gap = spacious ? 4 : 2;
        var minDetails = spacious ? 40 : density == BodyDensity.Full ? 28 : 25;
        IRenderable body;

        if (qr is not null && qr.Width + gap + minDetails <= innerWidth)
        {
            var inlineCaption = density != BodyDensity.Full;
            var showBottomBorder = unicode && (availableHeight >= QrHeight(qr, spacious, showCaption: !inlineCaption, showBottomBorder: true) + chromeLines || !mergeHeaderIntoPanel);
            var qrContent = Qr(qr, paired, spacious, unicode, showCaption: !inlineCaption, showBottomBorder: showBottomBorder);
            var detailsWidth = innerWidth - qr.Width - gap;
            var qrLines = QrHeight(qr, spacious, showCaption: !inlineCaption, showBottomBorder: showBottomBorder);
            var details = Details(localUrl, paired, view, activity, detailsWidth, spacious,
                minimal || density == BodyDensity.Minimal, qrLines, measureHeight,
                inlineCaption ? ScanCaption(paired) : null, omitPairingLink: density == BodyDensity.Minimal);
            if (density == BodyDensity.Compact && measureHeight(details, detailsWidth) > qrLines)
            {
                details = Details(localUrl, paired, view, activity, detailsWidth, spacious,
                    minimal: true, qrLines, measureHeight, inlineCaption ? ScanCaption(paired) : null, omitPairingLink: true);
            }

            var columns = new Grid();
            columns.AddColumn(new GridColumn { Width = detailsWidth, Padding = new Padding(0, 0, gap, 0) });
            columns.AddColumn(new GridColumn { Width = qr.Width, Padding = new Padding(0) });
            columns.AddRow(details, qrContent);
            body = columns;
        }
        else if (qr is not null && density == BodyDensity.Compact)
        {
            body = new Rows(CompactStackedDetails(localUrl, paired, view, activity), Qr(qr, paired, spacious: false, unicode, showCaption: true, showBottomBorder: unicode));
        }
        else if (qr is not null && density == BodyDensity.Minimal)
        {
            var showBottomBorder = unicode && (availableHeight >= QrHeight(qr, spacious: false, showCaption: false, showBottomBorder: true) + chromeLines + 1 || !mergeHeaderIntoPanel);
            var singleLine = availableHeight <= QrHeight(qr, spacious: false, showCaption: false, showBottomBorder: showBottomBorder) + chromeLines + 1;
            body = new Rows(MinimalStackedDetails(localUrl, paired, view, activity, singleLine), Qr(qr, paired, spacious: false, unicode, showCaption: false, showBottomBorder: showBottomBorder));
        }
        else
        {
            var details = Details(localUrl, paired, view, activity, innerWidth, spacious, minimal, targetHeight: 0, measureHeight);
            body = qr is not null ? new Rows(details, Text.Empty, Qr(qr, paired, spacious, unicode, showCaption: true, showBottomBorder: unicode)) : details;
        }

        if (!showQr && view.Qr is not null && (view.QrExpires is null || view.QrExpires > DateTimeOffset.UtcNow))
        {
            body = new Rows(body, new Text("Resize the terminal to show the QR; the link above also works.").Ellipsis());
        }

        return body;
    }

    private static int QrHeight(TerminalQr qr, bool spacious, bool showCaption = true, bool showBottomBorder = false)
    {
        var graphicLines = qr.Height > 0 ? qr.Height : qr.Graphic.Count(c => c == '\n') + 1;
        return graphicLines + (showBottomBorder ? 1 : 0) + (spacious ? 4 : showCaption ? 1 : 0);
    }

    private sealed class FixedWidth(IRenderable content, int width) : IRenderable
    {
        public Measurement Measure(RenderOptions options, int maxWidth) => new(Math.Min(width, maxWidth), Math.Min(width, maxWidth));
        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => content.Render(options, Math.Min(width, maxWidth));
    }

    private static IRenderable Header(CliView view, int width, bool spacious, bool paired, bool unicode, int animationFrame)
    {
        if (width < 24) return new Text(view.Status, CliTheme.Strong).Ellipsis();
        var statusWidth = Math.Min(16, Math.Max(1, width / 2));
        var header = new Grid { Width = width };
        header.AddColumn(new GridColumn { Width = width - statusWidth, Padding = new Padding(0) });
        header.AddColumn(new GridColumn { Width = statusWidth, Padding = new Padding(0), Alignment = Justify.Right });
        var brand = new Paragraph().Append(CliTheme.BrandPrefix(unicode), CliTheme.Accent).Append("tunnelite", CliTheme.Strong);
        if (spacious) brand.Append(paired ? "  /  paired access" : "  /  public tunnel");
        header.AddRow(brand, new Text(CliTheme.StatusPrefix(view.Status, unicode, animationFrame) + view.Status, CliTheme.StatusStyle(view.Status)));
        return header;
    }

    private static string ScanCaption(bool paired) =>
        paired ? "Scan to pair this browser" : "Scan to open on your phone";

    private static IRenderable Details(string localUrl, bool paired, CliView view, ActivitySnapshot activity,
        int width, bool spacious, bool minimal, int targetHeight, Func<IRenderable, int, int> height,
        string? inlineCaption = null, bool omitPairingLink = false)
    {
        var connection = new List<IRenderable>();
        if (!minimal) connection.Add(new Text("OPEN IN YOUR BROWSER", CliTheme.Strong));
        connection.Add(Address(view.PublicUrl ?? "Waiting for the relay...", primary: true));
        connection.Add(new Text(paired ? "Paired browsers only" : "Public link · anyone with the URL").Ellipsis());
        if (spacious) connection.Add(Text.Empty);
        if (!minimal) connection.Add(new Text("FORWARDS TO", CliTheme.Strong));
        connection.Add(Address(localUrl));
        if (paired && view.Status == "Connected" && view.PairingUrl is not null && !omitPairingLink)
        {
            if (spacious) connection.Add(Text.Empty);
            if (!minimal) connection.Add(new Text("PAIRING LINK", CliTheme.Strong));
            connection.Add(Address(view.PairingUrl));
        }
        if (paired && view.Status == "Connected" && view.QrExpires is { } expires)
        {
            var remaining = expires - DateTimeOffset.UtcNow;
            connection.Add(new Text(remaining > TimeSpan.Zero ? $"One browser · code expires in {remaining:mm\\:ss}"
                : "Pairing code expired · p for a new QR").Ellipsis());
        }
        if (inlineCaption is not null)
        {
            connection.Add(new Text(inlineCaption));
        }

        var traffic = new List<IRenderable>();
        if (spacious)
        {
            var stats = new Grid();
            var firstWidth = Math.Max(1, width / 2);
            stats.AddColumn(new GridColumn { Width = firstWidth, Padding = new Padding(0) });
            stats.AddColumn(new GridColumn { Width = Math.Max(1, width - firstWidth), Padding = new Padding(0) });
            stats.AddRow(new Text($"{activity.Requests:N0}", CliTheme.Strong),
                new Text($"{activity.Failures:N0}", new Style(activity.Failures > 0 ? Color.Red : Color.Default, decoration: Decoration.Bold)));
            stats.AddRow(new Text("requests"), new Text("proxy errors"));
            traffic.Add(stats);
            traffic.Add(Text.Empty);
        }
        else traffic.Add(new Text($"Requests {activity.Requests}   Proxy errors {activity.Failures}").Ellipsis());
        if (!minimal) traffic.Add(new Text("LATEST REQUEST", CliTheme.Strong));
        traffic.Add(new Text(activity.Latest).Ellipsis());
        if (activity.Errors.Length > 0 && spacious) traffic.Add(new Text("RECENT ERRORS", CliTheme.Error));
        foreach (var error in activity.Errors.TakeLast(spacious ? CliActivity.RecentErrorLimit : 1))
            traffic.Add(new Text(error.Message, CliTheme.Error).Ellipsis());
        if (view.Notice.Length > 0) traffic.Add(new Text(view.Notice).Ellipsis());

        var top = new Rows(connection);
        var bottom = new Rows(traffic);
        var minSpacer = spacious ? 2 : inlineCaption is not null ? 0 : 1;
        var spacer = targetHeight <= minSpacer ? minSpacer : Math.Max(minSpacer, targetHeight - height(top, width) - height(bottom, width));
        return new Rows(new[] { top }.Cast<IRenderable>().Concat(Enumerable.Repeat<IRenderable>(Text.Empty, spacer)).Append(bottom));
    }

    private static IRenderable CompactStackedDetails(string localUrl, bool paired, CliView view, ActivitySnapshot activity)
    {
        var rows = new List<IRenderable>
        {
            Address(view.PublicUrl ?? "Waiting for the relay...", primary: true),
            new Text($"{TerminalText.Clean(localUrl)} · {activity.Requests} req / {activity.Failures} err").Ellipsis(),
        };
        if (paired && view.Status == "Connected" && view.QrExpires is { } expires)
        {
            var remaining = expires - DateTimeOffset.UtcNow;
            rows.Add(new Text(remaining > TimeSpan.Zero ? $"One browser · code expires in {remaining:mm\\:ss}"
                : "Pairing code expired · p for a new QR").Ellipsis());
        }
        if (view.Notice.Length > 0) rows.Add(new Text(view.Notice).Ellipsis());
        return new Rows(rows);
    }

    private static IRenderable MinimalStackedDetails(string localUrl, bool paired, CliView view, ActivitySnapshot activity, bool singleLine) =>
        singleLine
            ? new Text(ScanCaption(paired)).Ellipsis()
            : new Rows(
                new Text(TerminalText.Clean(view.PublicUrl ?? localUrl, 2048), CliTheme.Accent).Ellipsis(),
                new Text($"{ScanCaption(paired)} · {activity.Requests} req / {activity.Failures} err").Ellipsis());

    private static IRenderable Address(string value, bool primary = false)
    {
        var clean = TerminalText.Clean(value, 2048);
        var link = Uri.TryCreate(clean, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? clean : null;
        return new Text(clean, new Style(primary ? CliTheme.Accent.Foreground : null,
            decoration: primary ? Decoration.Bold : Decoration.None, link: link)).Fold();
    }

    private static IRenderable Qr(TerminalQr qr, bool paired, bool spacious, bool unicode, bool showCaption = true, bool showBottomBorder = true)
    {
        var rows = new List<IRenderable>();
        if (spacious)
        {
            rows.Add(Align.Center(new Text(paired ? "PAIR YOUR BROWSER" : "OPEN ON YOUR PHONE", CliTheme.Strong)));
            rows.Add(Text.Empty);
        }
        rows.Add(new Text(unicode ? qr.Graphic.Replace(' ', '\u00A0') : qr.Graphic, CliTheme.QrInk));
        if (unicode && showBottomBorder)
        {
            rows.Add(new Text(new string('▀', qr.Width), CliTheme.QrBorder));
        }
        if (spacious) rows.Add(Text.Empty);
        if (showCaption)
        {
            rows.Add(Align.Center(new Text(ScanCaption(paired))));
        }
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
            rows.Add(new Text((selected ? "> " : "  ") + TerminalText.Clean(device.Name), selected ? CliTheme.Strong : Style.Plain).Ellipsis());
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
        var legend = new Paragraph();
        var bindings = CliTheme.KeyBindings(panel, paired, width, unicode);
        var keyPad = width < 54 ? "" : " ";
        var itemGap = width < 64 ? " " : "   ";
        for (var i = 0; i < bindings.Length; i++)
        {
            var (key, label) = bindings[i];
            legend.Append(keyPad + key + keyPad, CliTheme.Key).Append(" " + label + (i < bindings.Length - 1 ? itemGap : ""));
        }
        return legend;
    }
}
