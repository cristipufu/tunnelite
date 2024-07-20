#nullable disable
using System.Net;

namespace WebSocketTunnel.Server.Request
{
    public class ResponseMetadata
    {
        public Guid RequestId { get; set; }

        public Dictionary<string, string> Headers { get; set; }

        public Dictionary<string, string> ContentHeaders { get; set; }

        public HttpStatusCode StatusCode { get; set; }
    }
}
