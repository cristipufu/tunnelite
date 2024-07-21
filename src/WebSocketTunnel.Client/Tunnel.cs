#nullable disable
namespace WebSocketTunnel.Client
{
    public class Tunnel
    {
        public string Subdomain { get; set; }
        public Guid? ClientId { get; set; }
        public string LocalUrl { get; set; }
    }
}
