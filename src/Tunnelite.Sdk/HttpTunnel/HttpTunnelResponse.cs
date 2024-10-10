#nullable disable
namespace Tunnelite.Sdk;

public class HttpTunnelResponse
{
    public string TunnelUrl { get; set; }
    public string Subdomain { get; set; }
    public string Error { get; set; }
    public string Message { get; set; }
}
