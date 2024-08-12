#nullable disable
namespace WebSocketTunnel.Server.WsTunnel;

public class WsConnection
{
    public Guid RequestId { get; set; }

    public string Path { get; set; }
}
