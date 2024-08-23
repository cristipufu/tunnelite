#nullable disable
namespace Tunnelite.Client.HttpTunnel;

public class HttpConnection
{
    public Guid RequestId { get; set; }
    public string Method { get; set; }
    public string ContentType { get; set; }
    public string Path { get; set; }
}

public class WsConnection
{
    public Guid RequestId { get; set; }
    public string Path { get; set; }
}
