using System.Collections.Concurrent;

namespace WebSocketTunnel.Server
{
    public class TunnelStore
    {
        public ConcurrentDictionary<string, Tunnel> Tunnels = new();

        public ConcurrentDictionary<Guid, string> Connections = new();
    }

    public class Tunnel
    {
        public Guid InstanceId { get; set; }

        public string? LocalUrl { get; set; }
    }
}
