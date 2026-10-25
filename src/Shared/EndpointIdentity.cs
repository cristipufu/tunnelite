using System.Security.Cryptography;
using System.Text;

namespace Tunnelite.Protocol;

// Compiled into both assemblies. Protected names and version-8 client ids are verifiable without a database.
internal static class EndpointIdentity
{
    internal static byte[] ViewerKey(string secret) => HMACSHA256.HashData(Encoding.ASCII.GetBytes(secret), "tunnelite-browser-v1"u8);
    internal static bool IsProtected(Guid id) => (id.ToByteArray()[7] & 0xf0) == 0x80;
    internal static bool IsProtected(string name) => name.StartsWith("p-", StringComparison.OrdinalIgnoreCase);

    internal static bool TryDerive(string? secret, out Guid id, out string name)
    {
        id = default;
        name = "";
        if (secret is not { Length: 43 } || !secret.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(secret));
        name = "p-" + Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
        var bytes = hash[16..32];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x80);
        id = new Guid(bytes);
        return true;
    }
}
