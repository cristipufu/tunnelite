#nullable disable
namespace Tunnelite.Server.WsTunnel;

public class WsConnection
{
    public Guid RequestId { get; set; }

    public string Path { get; set; }

    /// <summary>
    /// Subprotocols the public client asked for (<c>Sec-WebSocket-Protocol</c>), so the tunnel client can request
    /// the same ones from the local app. Null when none were requested; older clients ignore the field.
    /// </summary>
    public string[] SubProtocols { get; set; }
}
