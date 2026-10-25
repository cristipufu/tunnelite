using System.Collections.Concurrent;

namespace Tunnelite.Server.HttpTunnel;

public class HttpTunnelStore
{
    // subdomain, [clientId, localUrl]
    public ConcurrentDictionary<string, HttpTunnelRequest> Tunnels = new();

    // clientId, connectionId
    public ConcurrentDictionary<Guid, string> Connections = new();

    // clientId, subdomain
    public ConcurrentDictionary<Guid, string> Clients = new();
}

public class HttpTunnelRequest
{
    public bool RequirePairing { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[]? ViewerKey { get; set; }
    public int ProtocolVersion { get; set; }
    public string? Subdomain { get; set; }

    public Guid ClientId { get; set; }

    public string? LocalUrl { get; set; }
}
