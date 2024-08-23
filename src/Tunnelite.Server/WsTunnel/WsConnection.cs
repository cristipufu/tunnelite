#nullable disable
namespace Tunnelite.Server.WsTunnel;

public class WsConnection
{
    public Guid RequestId { get; set; }

    public string Path { get; set; }
}
