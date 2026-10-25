#nullable disable
namespace Tunnelite.Sdk;

public class HttpTunnelRequest
{
    /// <summary>Version 2 forwards SSE through the streaming HTTP path, including response status and headers.</summary>
    public int ProtocolVersion { get; set; } = 2;
    public string Subdomain { get; set; }
    public Guid? ClientId { get; set; }
    public string LocalUrl { get; set; }
    public string PublicUrl { get; set; }
}
