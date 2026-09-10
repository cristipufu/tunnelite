#nullable disable
using Nerdbank.MessagePack;
using System.Net.WebSockets;

namespace Tunnelite.Sdk;

/// <summary>
/// A chunk of WebSocket traffic on its way through the tunnel.
/// </summary>
/// <remarks>
/// Serialized as a three element array (<c>[data, type, endOfMessage]</c>) - hence the explicit keys - which is
/// what the server binds on its side. It is a class rather than a tuple because SignalR resolves the
/// type of a stream item at runtime, and under NativeAOT that resolution only works for reference types.
/// </remarks>
public class WsChunk
{
    public WsChunk()
    {
    }

    public WsChunk(byte[] data, WebSocketMessageType type, bool endOfMessage = true)
    {
        Data = data;
        Type = type;
        EndOfMessage = endOfMessage;
    }

    [Key(0)]
    public byte[] Data { get; set; }

    [Key(1)]
    public WebSocketMessageType Type { get; set; }

    /// <summary>
    /// Whether this chunk ends a WebSocket message, so fragmented messages are reassembled with the same
    /// boundaries on the other side of the tunnel.
    /// </summary>
    [Key(2)]
    public bool EndOfMessage { get; set; } = true;
}
