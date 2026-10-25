using System.Collections;
using QRCoder;

namespace Tunnelite.Client;

internal sealed record TerminalQr(string Url, string Graphic, int Width, int Height = 0)
{
    internal static TerminalQr Create(string url, bool unicode)
    {
        using var data = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.L);
        TrimQuietZone(data, quietModules: 1);
        using var code = new AsciiQRCode(data);
        var graphic = (unicode ? code.GetGraphicSmall(invert: true)
            : code.GetGraphic(1, darkColorString: "##", whiteSpaceString: "  ")).TrimEnd('\r', '\n');
        var lines = graphic.Split('\n');
        if (unicode && lines.Length > 18 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines = lines[..^1];
            graphic = string.Join('\n', lines);
        }
        return new(url, graphic, lines.Max(line => line.TrimEnd('\r').Length), lines.Length);
    }

    private static void TrimQuietZone(QRCodeData data, int quietModules)
    {
        const int defaultQuietZone = 4;
        var trim = defaultQuietZone - quietModules;
        if (trim <= 0 || data.ModuleMatrix.Count <= trim * 2) return;

        var size = data.ModuleMatrix.Count - trim * 2;
        var trimmed = new List<BitArray>(size);
        for (var y = trim; y < data.ModuleMatrix.Count - trim; y++)
        {
            var sourceRow = data.ModuleMatrix[y];
            var row = new BitArray(size);
            for (var x = 0; x < size; x++)
            {
                row[x] = sourceRow[x + trim];
            }
            trimmed.Add(row);
        }

        data.ModuleMatrix = trimmed;
    }
}
