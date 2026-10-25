using Tunnelite.Sdk;

namespace Tunnelite.AspNetCore;

/// <summary>Configuration for an app-owned tunnel. The application owns desktop identity and pairing persistence.</summary>
public sealed class TunneliteOptions
{
    public string PublicUrl { get; set; } = "https://tunnelite.com";
    public string? LocalUrl { get; set; }
    public PairedAccess? Access { get; set; }
    public TunnelIdentity? Identity { get; set; }
    public bool? AllowUntrustedLocalCertificates { get; set; }
    /// <summary>Called after registration and after reconnects, when the endpoint URL is ready.</summary>
    public Func<HttpTunnelClient, CancellationToken, Task>? OnReady { get; set; }
}
