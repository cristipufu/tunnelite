using System.Security.Cryptography;
using System.Text;

namespace Tunnelite.Protocol;

internal static class TunnelProtocol
{
    /// <summary>
    /// Largest payload placed in a single SignalR stream item. Kept well under
    /// <c>MaximumReceiveMessageSize</c> (and the 32 KB SignalR default) so a chunk plus its
    /// MessagePack envelope never trips the limit.
    /// </summary>
    internal const int ChunkSize = 16 * 1024;
}

// Compiled into both assemblies. Protected names and version-8 client ids are verifiable without a database.
internal static class EndpointIdentity
{
    internal const int SecretLength = 43;
    internal const string ProtectedPrefix = "p-";
    private const byte VersionMask = 0xf0;
    private const byte Version8 = 0x80;

    internal static byte[] ViewerKey(string secret) => HMACSHA256.HashData(Encoding.ASCII.GetBytes(secret), "tunnelite-browser-v1"u8);
    internal static bool IsProtected(Guid id) => (id.ToByteArray()[7] & VersionMask) == Version8;
    internal static bool IsProtected(string name) => name.StartsWith(ProtectedPrefix, StringComparison.OrdinalIgnoreCase);

    internal static bool TryDerive(string? secret, out Guid id, out string name)
    {
        id = default;
        name = "";
        if (secret is not { Length: SecretLength } || !secret.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(secret));
        name = ProtectedPrefix + Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
        var bytes = hash[16..32];
        bytes[7] = (byte)((bytes[7] & 0x0f) | Version8);
        id = new Guid(bytes);
        return true;
    }
}
