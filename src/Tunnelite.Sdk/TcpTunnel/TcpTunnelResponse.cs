#nullable disable
namespace Tunnelite.Sdk;

public class TcpTunnelResponse
{
    public string TunnelUrl { get; set; }
    public int Port { get; set; }
    public string Error { get; set; }
    public string Message { get; set; }
}
