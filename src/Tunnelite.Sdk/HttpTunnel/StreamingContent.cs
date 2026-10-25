using System.Net;

namespace Tunnelite.Sdk;

// Flush each read so small SSE frames are not held in HttpClient's outgoing request buffer.
internal sealed class StreamingContent(Stream source) : HttpContent
{
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer)) != 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read));
            await stream.FlushAsync();
        }
    }

    protected override bool TryComputeLength(out long length) { length = 0; return false; }
}
