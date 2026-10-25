using System.Net;

namespace Tunnelite.Sdk;

// Flush each read so small SSE frames are not held in HttpClient's outgoing request buffer.
internal sealed class StreamingContent(Stream source) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => CopyAsync(stream, CancellationToken.None);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => CopyAsync(stream, cancellationToken);

    private async Task CopyAsync(Stream stream, CancellationToken cancellationToken)
    {
        await stream.FlushAsync(cancellationToken);
        var buffer = new byte[TunnelProtocol.ChunkSize];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }

    protected override bool TryComputeLength(out long length) { length = 0; return false; }
}
