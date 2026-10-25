using Spectre.Console;
using Tunnelite.Client;

namespace Tunnelite.Tests;

public class CliDisplayTests
{
    private static (CliDisplay Display, IAnsiConsole Console, StringWriter Output) Create(int width, int height, bool paired = false)
    {
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(output), Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No,
            // CI profile enrichers otherwise re-enable ANSI, so string lengths include escape sequences.
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
        });
        console.Profile.Width = width;
        console.Profile.Height = height;
        return (new CliDisplay(console, "http://localhost:1430", paired, live: true), console, output);
    }

    [Theory]
    [InlineData(120, 40)]
    [InlineData(180, 50)]
    public void Dashboard_has_outer_gutters_a_top_inset_and_a_bounded_content_width(int width, int height)
    {
        var (display, console, output) = Create(width, height);
        display.SetView(new("Connected", "https://example.tunnelite.com",
            TerminalQr.Create("https://example.tunnelite.com", true)));
        console.Write(display.Render());
        var lines = output.ToString().Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var drawn = lines.Select((line, row) => (line, row)).Where(item => !string.IsNullOrWhiteSpace(item.line)).ToArray();
        Assert.InRange(drawn[0].row, 2, 3);
        var left = drawn.Min(item => item.line.TakeWhile(char.IsWhiteSpace).Count());
        var right = drawn.Max(item => item.line.TrimEnd().Length - 1);
        Assert.True(left >= 4, output.ToString());
        Assert.True(right < width - 4, output.ToString());
        Assert.True(right - left + 1 <= 112, output.ToString());
    }

    [Theory]
    [InlineData(120, 40, true)]
    [InlineData(35, 12, false)]
    [InlineData(80, 16, false)]
    public void A_qr_is_shown_whole_or_replaced_with_a_resize_message(int width, int height, bool visible)
    {
        var (display, console, output) = Create(width, height);
        var qr = TerminalQr.Create("https://example.tunnelite.com", unicode: true);
        display.SetView(new("Connected", "https://example.tunnelite.com", qr));
        console.Write(display.Render());
        var rendered = output.ToString();
        Assert.Equal(visible, rendered.Contains("Scan to open on your phone"));
        Assert.Equal(!visible, rendered.Contains("Resize the terminal"));
        Assert.Matches(@"q\s+Quit", rendered);
        Assert.DoesNotContain("Revoke", rendered);
    }

    [Fact]
    public void Device_names_and_request_paths_cannot_inject_terminal_controls_or_markup()
    {
        var (display, console, output) = Create(100, 30, paired: true);
        display.Request("GET", "https://localhost:1430/[red]test?secret=hidden");
        display.Error("error\u001b[2J\r\n\u202ehidden");
        console.Write(display.Render());
        var rendered = output.ToString();
        Assert.Contains("[red]test", rendered);
        Assert.DoesNotContain("secret", rendered);
        Assert.DoesNotContain("\u001b[2J", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", TerminalText.Clean("error\u001b[2J\r\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("\u202e", rendered, StringComparison.Ordinal);
        Assert.Matches(@"p\s+New QR", rendered);
    }

    [Fact]
    public void A_pairing_link_remains_complete_when_the_qr_cannot_fit()
    {
        var (display, console, output) = Create(50, 18, paired: true);
        var link = "https://example.tunnelite.com/_tunnelite/pair#code=" + new string('a', 43);
        display.SetView(new("Connected", "https://example.tunnelite.com", TerminalQr.Create(link, true), PairingUrl: link));
        console.Write(display.Render());
        Assert.Contains(link, System.Text.RegularExpressions.Regex.Replace(output.ToString(), @"\s+", ""));
    }

    [Fact]
    public void Device_labels_are_literal_text_and_cannot_clear_the_screen()
    {
        var (display, console, output) = Create(100, 30, paired: true);
        display.ShowDevices(DevicePanel.Revoke,
            [new(Guid.NewGuid(), "[red]Phone\u001b[2J", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), "hash")]);
        console.Write(display.Render());
        Assert.Contains("[red]Phone", output.ToString());
        Assert.DoesNotContain("\u001b[2J", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Enrollment_changes_cannot_switch_the_device_selected_for_revocation()
    {
        var first = new Tunnelite.Sdk.PairedDevice(Guid.NewGuid(), "First", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), "hash");
        var second = first with { Id = Guid.NewGuid(), Name = "Second" };
        var view = new CliView("Connected", Panel: DevicePanel.Revoke, SelectedDevice: 1, Devices: [first, second]);
        view = view.WithDevices([second, first]);
        Assert.Equal(second.Id, view.Devices![view.SelectedDevice].Id);
        view = view.WithDevices([first]);
        Assert.Equal(-1, view.SelectedDevice);
    }

    [Fact]
    public void An_expired_pairing_code_is_hidden_until_the_user_generates_another()
    {
        var (display, console, output) = Create(140, 50, paired: true);
        display.SetView(new("Connected", "https://example.tunnelite.com",
            TerminalQr.Create("https://example.tunnelite.com/_tunnelite/pair#code=old", true), QrExpires: DateTimeOffset.UtcNow.AddSeconds(-1)));
        console.Write(display.Render());
        Assert.Contains("Pairing code expired", output.ToString());
        Assert.DoesNotContain("Scan to pair", output.ToString());
    }

    [Fact]
    public void Switching_from_device_list_to_revoke_preserves_the_selected_device()
    {
        var (display, _, _) = Create(100, 30, paired: true);
        var first = new Tunnelite.Sdk.PairedDevice(Guid.NewGuid(), "First", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), "hash");
        var second = first with { Id = Guid.NewGuid(), Name = "Second" };
        IReadOnlyList<Tunnelite.Sdk.PairedDevice> devices = [first, second];

        display.ShowDevices(DevicePanel.List, devices);
        Assert.Equal(0, display.View.SelectedDevice);

        display.SetView(display.View with { SelectedDevice = 1 });
        display.ShowDevices(DevicePanel.Revoke, devices);
        Assert.Equal(1, display.View.SelectedDevice);
        Assert.Equal(DevicePanel.Revoke, display.View.Panel);
    }

    [Fact]
    public void Status_prefix_animates_spinner_while_connecting_and_stays_static_when_connected()
    {
        Assert.Equal("● ", CliTheme.StatusPrefix("Connected", unicode: true, animationFrame: 3));
        Assert.Equal("* ", CliTheme.StatusPrefix("Connected", unicode: false, animationFrame: 3));
        Assert.NotEqual("● ", CliTheme.StatusPrefix("Connecting", unicode: true, animationFrame: 1));
        Assert.NotEqual("* ", CliTheme.StatusPrefix("Reconnecting", unicode: false, animationFrame: 1));
    }

    [Theory]
    [InlineData(100, 26)]
    [InlineData(80, 24)]
    [InlineData(66, 25)]
    [InlineData(52, 29)]
    [InlineData(50, 26)]
    [InlineData(45, 25)]
    public void Paired_qr_fits_across_smaller_and_narrower_terminal_resolutions(int width, int height)
    {
        var (display, console, output) = Create(width, height, paired: true);
        var publicUrl = "https://p-8f3c1cd768f8c84f489d7f7da3a82d35.tunnelite.com";
        var pairingUrl = publicUrl + "/_tunnelite/pair#code=" + new string('a', 43);
        var qr = TerminalQr.Create(pairingUrl, unicode: true);
        display.SetView(new("Connected", publicUrl, qr, PairingUrl: pairingUrl));
        console.Write(display.Render());
        var rendered = output.ToString();
        Assert.Contains("CONNECTION", rendered);
        Assert.Contains("Scan to pair this browser", rendered);
        Assert.DoesNotContain("Resize the terminal", rendered);
        if (height >= 26)
        {
            Assert.Contains(new string('▀', qr.Width), rendered);
        }
    }
}
