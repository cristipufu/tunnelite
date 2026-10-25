using System.Buffers;
using System.Net;

namespace Tunnelite.Sdk;

// Flush each read so small SSE frames are not held in HttpClient's outgoing request buffer.
internal sealed class StreamingContent(Stream source, CancellationToken transferCancellation = default) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => CopyAsync(stream, transferCancellation);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        if (!transferCancellation.CanBeCanceled) return CopyAsync(stream, cancellationToken);
        return CopyLinkedAsync(stream, cancellationToken);
    }

    private async Task CopyLinkedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(transferCancellation, cancellationToken);
        await CopyAsync(stream, linked.Token);
    }

    private async Task CopyAsync(Stream stream, CancellationToken cancellationToken)
    {
        await stream.FlushAsync(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(TunnelProtocol.ChunkSize);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, TunnelProtocol.ChunkSize), cancellationToken)) != 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    protected override bool TryComputeLength(out long length) { length = 0; return false; }
}
