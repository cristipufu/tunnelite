using System.Net.WebSockets;

namespace Tunnelite.Server.WsTunnel;

public class WsDefferedRequest
{
    public string? ConnectionId { get; set; }
    public WebSocket? WebSocket { get; set; }

    public Guid RequestId { get; set; }

    public TaskCompletionSource? TaskCompletionSource { get; set; }
}
