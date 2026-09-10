using System.Net.WebSockets;

namespace Tunnelite.Tests.Infrastructure;

public static class TestData
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Deterministic, non-repeating-looking payload so truncation or reordering shows up in comparisons.</summary>
    public static byte[] Bytes(int count, int seed = 42)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Reads one complete WebSocket message, however many frames it arrives in.</summary>
    public static async Task<(byte[] Data, WebSocketMessageType Type)> ReceiveMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[64 * 1024];
        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return (stream.ToArray(), result.MessageType);
    }

    public static async Task ReadExactlyAsync(Stream stream, byte[] destination, CancellationToken cancellationToken)
    {
        var offset = 0;

        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination.AsMemory(offset), cancellationToken);

            if (read == 0)
            {
                throw new EndOfStreamException($"Stream ended after {offset} of {destination.Length} bytes.");
            }

            offset += read;
        }
    }
}
