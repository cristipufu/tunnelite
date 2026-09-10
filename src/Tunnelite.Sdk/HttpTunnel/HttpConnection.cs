#nullable disable
namespace Tunnelite.Sdk;

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

    /// <summary>
    /// Subprotocols the public client asked for; requested from the local app as well. Null from servers that
    /// predate the field.
    /// </summary>
    public string[] SubProtocols { get; set; }
}

public class SseConnection : HttpConnection
{
    public string Content { get; set; }
}
