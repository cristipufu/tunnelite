namespace Tunnelite.Server.SseTunnel;

public class SseDeferredRequest
{
    public string? ConnectionId { get; set; }
    public HttpContext? HttpContext { get; set; }
    public Guid RequestId { get; set; }
    public TaskCompletionSource? TaskCompletionSource { get; set; }
}
