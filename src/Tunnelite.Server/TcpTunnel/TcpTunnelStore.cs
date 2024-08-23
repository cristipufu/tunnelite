using System.Collections.Concurrent;

#nullable disable
namespace Tunnelite.Server.TcpTunnel;

public class TcpTunnelStore
{
    // clientId, connectionId
    public ConcurrentDictionary<Guid, string> Connections = new();
}

public class TcpTunnelRequest
{
    public int LocalPort { get; set; }
    public int? PublicPort { get; set; }
    public string Host { get; set; }
    public Guid ClientId { get; set; }
    public string LocalUrl { get; set; }
}

public class TcpTunnelResponse
{
    public string TunnelUrl { get; set; }
    public int Port { get; set; }
    public string Error { get; set; }
    public string Message { get; set; }
}
