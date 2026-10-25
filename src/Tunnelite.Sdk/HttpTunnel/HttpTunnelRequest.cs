#nullable disable
namespace Tunnelite.Sdk;

public class HttpTunnelRequest
{
    /// <summary>Override local certificate policy. Null preserves legacy HTTP tunnel behavior and verifies certificates in paired mode. Explicit bypass is loopback-only. Relay TLS is always verified.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool? AllowUntrustedLocalCertificates { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public PairedAccess Access { get; set; }
    public bool RequirePairing => Access is not null;
    /// <summary>The desktop identity secret; sent only as an authorization header to the relay.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string OwnerToken { get; set; }
    /// <summary>Version 2 forwards SSE through the streaming HTTP path, including response status and headers.</summary>
    public int ProtocolVersion { get; set; } = 2;
    /// <summary>The active SignalR connection, populated by HttpTunnelClient during registration.</summary>
    public string ConnectionId { get; set; }
    public string Subdomain { get; set; }
    public Guid? ClientId { get; set; }
    public string LocalUrl { get; set; }
    public string PublicUrl { get; set; }
}
