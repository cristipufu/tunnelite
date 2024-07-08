using System.Net;

namespace WebSocketTunnel.Client
{
    public class HttpContentCallback(Func<Stream, CancellationToken, Task> callback) : HttpContent
    {
        private readonly Func<Stream, CancellationToken, Task> _callback = callback;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return SerializeToStreamAsync(stream, context, CancellationToken.None);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        {
            return _callback(stream, token);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
