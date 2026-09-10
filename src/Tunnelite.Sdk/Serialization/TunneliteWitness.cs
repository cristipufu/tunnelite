using Nerdbank.MessagePack;
using PolyType;
using System.Net.WebSockets;

namespace Tunnelite.Sdk;

/// <summary>
/// Type shapes for everything that crosses the tunnel hub. SignalR resolves argument and stream item
/// types at runtime, so an AOT build needs the shapes generated up front rather than discovered by
/// reflection.
/// </summary>
[GenerateShapeFor<HttpConnection>]
[GenerateShapeFor<WsConnection>]
[GenerateShapeFor<SseConnection>]
[GenerateShapeFor<TcpConnection>]
[GenerateShapeFor<TcpTunnelRequest>]
[GenerateShapeFor<TcpTunnelResponse>]
[GenerateShapeFor<WsChunk>]
[GenerateShapeFor<byte[]>]
[GenerateShapeFor<string>]
// Non-generic InvokeAsync asks the protocol to deserialize the completion as System.Object. The server
// answers a Task-returning hub method with a null result, so without this shape every upload stream
// (SSE, WebSocket, TCP) ended with "does not support type 'System.Object'".
[GenerateShapeFor<object>]
internal partial class TunneliteWitness;

/// <summary>
/// Keeps the payload encoding identical to the one MessagePack-CSharp produces on the server:
/// Guids as their 36 character string form rather than an extension, and enums as their names.
/// </summary>
internal sealed class GuidAsStringConverter : MessagePackConverter<Guid>
{
    public override Guid Read(ref MessagePackReader reader, SerializationContext context) =>
        reader.TryReadNil() ? Guid.Empty : Guid.Parse(reader.ReadString()!);

    public override void Write(ref MessagePackWriter writer, in Guid value, SerializationContext context) =>
        writer.Write(value.ToString());
}

internal sealed class WebSocketMessageTypeConverter : MessagePackConverter<WebSocketMessageType>
{
    public override WebSocketMessageType Read(ref MessagePackReader reader, SerializationContext context) =>
        Enum.Parse<WebSocketMessageType>(reader.ReadString()!);

    public override void Write(ref MessagePackWriter writer, in WebSocketMessageType value, SerializationContext context) =>
        writer.Write(value.ToString());
}
