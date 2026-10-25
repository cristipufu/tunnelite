using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Tunnelite.Protocol;

internal static class ViewerTicket
{
    internal static string CookieName(bool secure) => secure ? "__Host-tunnelite" : "tunnelite_session";
    internal static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static string Create(byte[] key, Guid id, DateTimeOffset expires, string token)
    {
        var payload = $"{id:N}.{expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}.{token}";
        return payload + "." + Encode(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(payload)));
    }

    internal static bool Validate(byte[] key, string? ticket, DateTimeOffset now, out Guid id, out string token)
    {
        id = default;
        token = "";
        if (ticket is null || ticket.Length > 200) return false;
        var parts = ticket.Split('.');
        if (parts.Length != 4 || !Guid.TryParseExact(parts[0], "N", out id)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expiry)
            || expiry <= now.ToUnixTimeSeconds() || parts[2].Length != 43 || parts[3].Length != 43) return false;
        var payload = ticket[..ticket.LastIndexOf('.')];
        var expected = Encode(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(payload)));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[3]))) return false;
        token = parts[2];
        return true;
    }

    internal static string? Cookie(string? header, bool secure)
    {
        var name = CookieName(secure) + "=";
        return header?.Split(';').Select(s => s.Trim()).FirstOrDefault(s => s.StartsWith(name, StringComparison.Ordinal))?[name.Length..];
    }
}
