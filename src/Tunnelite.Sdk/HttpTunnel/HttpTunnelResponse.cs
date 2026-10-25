#nullable disable
namespace Tunnelite.Sdk;

public class HttpTunnelResponse
{
    public bool SupportsWebSocketClose { get; set; }
    public bool ProtectedEndpoint { get; set; }
    public bool RequirePairing { get; set; }
    public string TunnelUrl { get; set; }
    public string Subdomain { get; set; }
    public string Error { get; set; }
    public string Message { get; set; }
}
