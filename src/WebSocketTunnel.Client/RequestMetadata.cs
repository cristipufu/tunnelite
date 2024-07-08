#nullable disable
namespace WebSocketTunnel.Client
{
    public class RequestMetadata
    {
        public Guid RequestId { get; set; }

        public string Method { get; set; }

        public string ContentType { get; set; }

        public long? ContentLength { get; set; }

        public Dictionary<string, string> Headers { get; set; }

        public string Path { get; set; }
    }
}
