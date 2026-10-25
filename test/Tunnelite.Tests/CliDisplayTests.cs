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
        });
        console.Profile.Width = width;
        console.Profile.Height = height;
        return (new CliDisplay(console, "http://localhost:1430", paired, live: true), console, output);
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
        Assert.Contains("q Quit", rendered);
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
        Assert.Contains("p New QR", rendered);
    }

    [Fact]
    public void A_pairing_link_remains_complete_when_the_qr_cannot_fit()
    {
        var (display, console, output) = Create(50, 18, paired: true);
        var link = "https://example.tunnelite.com/_tunnelite/pair#code=" + new string('a', 43);
        display.SetView(new("Connected", "https://example.tunnelite.com", TerminalQr.Create(link, true), PairingUrl: link));
        console.Write(display.Render());
        Assert.Contains(link, output.ToString().Replace("\r", "").Replace("\n", ""));
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
}
