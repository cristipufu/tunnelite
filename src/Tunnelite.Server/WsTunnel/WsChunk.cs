using MessagePack;
using System.Net.WebSockets;

namespace Tunnelite.Server.WsTunnel;

/// <summary>
/// One WebSocket frame travelling through the tunnel, serialized as a MessagePack array.
/// </summary>
/// <remarks>
/// Clients built before the flag existed send a two element array (<c>[data, type]</c>), which MessagePack
/// deserializes into this type with <see cref="EndOfMessage"/> left <c>null</c>; that is read as <c>true</c>,
/// which is exactly what those clients assumed when they sent the frame. Current clients send the third element
/// so that fragmented messages are reassembled correctly on the other side.
/// </remarks>
[MessagePackObject]
public class WsChunk
{
    [Key(0)]
    public byte[] Data { get; set; } = [];

    [Key(1)]
    public WebSocketMessageType Type { get; set; }

    [Key(2)]
    public bool? EndOfMessage { get; set; }

    [IgnoreMember]
    public bool IsEndOfMessage => EndOfMessage ?? true;
}
