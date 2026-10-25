using System.Globalization;
using System.Text;

namespace Tunnelite.Client;

internal static class TerminalText
{
    internal static string Clean(string? value, int limit = 512)
    {
        var text = new StringBuilder();
        foreach (var rune in (value ?? "").EnumerateRunes())
        {
            if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format) continue;
            if (text.Length + rune.Utf16SequenceLength > limit) break;
            text.Append(rune);
        }
        return text.ToString();
    }

    internal static string Request(string method, string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri)) path = uri.AbsolutePath;
        else path = path.Split('?', '#')[0];
        return Clean(method, 12) + " " + Clean(path, 240);
    }
}
