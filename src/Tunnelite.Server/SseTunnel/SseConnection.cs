#nullable disable
using Tunnelite.Server.HttpTunnel;

namespace Tunnelite.Server.SseTunnel;

public class SseConnection : HttpConnection
{
    public string Content { get; set; }
}
