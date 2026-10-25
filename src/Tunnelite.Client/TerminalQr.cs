using QRCoder;

namespace Tunnelite.Client;

internal sealed record TerminalQr(string Url, string Graphic, int Width, int Height = 0)
{
    internal static TerminalQr Create(string url, bool unicode)
    {
        using var data = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var code = new AsciiQRCode(data);
        var graphic = (unicode ? code.GetGraphicSmall(invert: true)
            : code.GetGraphic(1, darkColorString: "##", whiteSpaceString: "  ")).TrimEnd('\r', '\n');
        var lines = graphic.Split('\n');
        return new(url, graphic, lines.Max(line => line.TrimEnd('\r').Length), lines.Length);
    }
}
