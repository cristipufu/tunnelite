#nullable disable
namespace WebSocketTunnel.Client
{
    public class TunnelResponse
    {
        public string TunnelUrl { get; set; }
        public string Subdomain { get; set; }
        public string Error { get; set; }
        public string Message { get; set; }
    }
}
