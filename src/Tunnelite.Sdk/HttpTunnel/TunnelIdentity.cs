using System.Security.Cryptography;
using Tunnelite.Protocol;

namespace Tunnelite.Sdk;

/// <summary>A desktop-owned secret that recreates the same protected address after either side restarts.</summary>
public sealed class TunnelIdentity
{
    /// <summary>The secret to keep on the desktop. Never include it in a browser link.</summary>
    public string Secret { get; }
    public Guid ClientId { get; }
    public string Subdomain { get; }

    public TunnelIdentity(string secret)
    {
        if (!EndpointIdentity.TryDerive(secret, out var id, out var name))
            throw new ArgumentException("The identity must be a 32-byte base64url secret.", nameof(secret));
        Secret = secret;
        ClientId = id;
        Subdomain = name;
    }

    public static TunnelIdentity Create() => new(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_'));
}
