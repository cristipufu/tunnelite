using System.Collections.Concurrent;

namespace WebSocketTunnel.Server
{
    public class TunnelStore
    {
        // subdomain, [clientId, localUrl]
        public ConcurrentDictionary<string, Tunnel> Tunnels = new();

        // clientId, connectionId
        public ConcurrentDictionary<Guid, string> Connections = new();

        // clientId, subdomain
        public ConcurrentDictionary<Guid, string> Clients = new();
    }

    public class Tunnel
    {
        public Guid ClientId { get; set; }

        public string? LocalUrl { get; set; }
    }
}
