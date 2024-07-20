using Microsoft.AspNetCore.SignalR;
using System.Runtime.CompilerServices;
using WebSocketTunnel.Server.Infrastructure;

namespace WebSocketTunnel.Server
{
    public class TunnelHub(RequestsQueue requestsQueue, TunnelStore tunnelStore) : Hub
    {
        private readonly RequestsQueue _requestsQueue = requestsQueue;
        private readonly TunnelStore _tunnelStore = tunnelStore;

        public override Task OnConnectedAsync()
        {
            var clientId = Context.GetHttpContext()!.Request.Query["clientId"].ToString();

            _tunnelStore.Connections.AddOrUpdate(Guid.Parse(clientId), Context.ConnectionId, (key, oldValue) => Context.ConnectionId);

            return base.OnConnectedAsync();
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            var clientIdQuery = Context.GetHttpContext()!.Request.Query["clientId"].ToString();

            var clientId = Guid.Parse(clientIdQuery);

            if (_tunnelStore.Clients.TryGetValue(clientId, out var subdomain))
            {
                _tunnelStore.Tunnels.Remove(subdomain, out var _);
                _tunnelStore.Connections.Remove(clientId, out var _);
                _tunnelStore.Clients.Remove(clientId, out _);
            }

            return base.OnDisconnectedAsync(exception);
        }

        public async IAsyncEnumerable<byte[]> StreamRequestBodyAsync(Guid requestId, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var httpContext = _requestsQueue.GetHttpContext(requestId);

            if (httpContext == null)
            {
                yield break;
            }

            const int chunkSize = 512 * 1024; // 512KB

            var buffer = new byte[chunkSize];
            int bytesRead;

            while ((bytesRead = await httpContext.Request.Body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (bytesRead == buffer.Length)
                {
                    yield return buffer;
                }
                else
                {
                    var chunk = new byte[bytesRead];

                    Array.Copy(buffer, chunk, bytesRead);

                    yield return chunk;
                }
            }
        }

        public async Task StreamResponseBodyAsync(ResponseMetadata responseMetadata, IAsyncEnumerable<byte[]> stream)
        {
            var httpContext = _requestsQueue.GetHttpContext(responseMetadata.RequestId);

            if (httpContext == null)
            {
                return;
            }

            try
            {
                httpContext.Response.StatusCode = (int)responseMetadata.StatusCode;

                var notAllowed = new string[] { "Connection", "Transfer-Encoding", "Keep-Alive", "Upgrade", "Proxy-Connection" };

                foreach (var header in responseMetadata.Headers)
                {
                    if (!notAllowed.Contains(header.Key))
                    {
                        httpContext.Response.Headers.TryAdd(header.Key, header.Value);
                    }
                }

                foreach (var header in responseMetadata.ContentHeaders)
                {
                    if (!notAllowed.Contains(header.Key))
                    {
                        httpContext.Response.Headers.TryAdd(header.Key, header.Value);
                    }
                }

                var responseStream = httpContext.Response.Body;

                await foreach (var chunk in stream)
                {
                    await responseStream.WriteAsync(chunk);
                }
            }
            catch (Exception ex)
            {
                httpContext.Response.StatusCode = 500;

                await httpContext.Response.WriteAsync($"An error occurred while tunneling the request: {ex.Message}");
            }

            await _requestsQueue.CompleteAsync(responseMetadata.RequestId);
        }

        public async Task CompleteWithErrorAsync(RequestMetadata requestMetadata, string message)
        {
            var httpContext = _requestsQueue.GetHttpContext(requestMetadata.RequestId);

            if (httpContext == null)
            {
                return;
            }

            httpContext.Response.StatusCode = 500;

            await httpContext.Response.WriteAsync($"An error occurred while tunneling the request: {message}");

            await _requestsQueue.CompleteAsync(requestMetadata.RequestId);
        }
    }
}
