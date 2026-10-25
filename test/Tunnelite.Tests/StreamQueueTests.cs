using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Tunnelite.Server.HttpTunnel;
using Tunnelite.Server.SseTunnel;
using Tunnelite.Server.WsTunnel;

namespace Tunnelite.Tests;

public class StreamQueueTests
{
    [Fact]
    public void Loopback_host_resolution_rejects_remote_ips_and_public_subdomains()
    {
        var loopback = new DefaultHttpContext();
        loopback.Request.Host = new HostString("localhost", 5000);
        loopback.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        Assert.True(HttpTunnelStore.IsLoopbackHost(loopback));

        var spoofedRemote = new DefaultHttpContext();
        spoofedRemote.Request.Host = new HostString("localhost", 5000);
        spoofedRemote.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.10");
        Assert.False(HttpTunnelStore.IsLoopbackHost(spoofedRemote));

        var subdomain = new DefaultHttpContext();
        subdomain.Request.Host = new HostString("localhost.tunnelite.com");
        subdomain.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        Assert.False(HttpTunnelStore.IsLoopbackHost(subdomain));
    }

    [Fact]
    public async Task Http_response_can_only_be_claimed_once_for_a_pending_request()
    {
        var queue = new HttpRequestsQueue();
        var requestId = Guid.NewGuid();
        var context = new DefaultHttpContext();

        var waiting = queue.WaitForCompletionAsync(requestId, context);

        Assert.Same(context, queue.ClaimResponseHttpContext(requestId));
        Assert.Null(queue.ClaimResponseHttpContext(requestId));

        await queue.CompleteAsync(requestId);
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Disconnect_cleanup_is_scoped_to_the_client_and_connection(bool webSockets)
    {
        var ws = new WsRequestsQueue();
        var sse = new SseRequestsQueue();
        using var oldSocket = new ClientWebSocket();
        using var currentSocket = new ClientWebSocket();
        using var otherSocket = new ClientWebSocket();
        var client = Guid.NewGuid();
        var otherClient = Guid.NewGuid();
        var request = Guid.NewGuid();
        var currentRequest = Guid.NewGuid();
        // Reusing a request id under another client must not allow cross-client completion.
        var old = webSockets ? ws.WaitForCompletionAsync(client, request, oldSocket, "old")
            : sse.WaitForCompletionAsync(client, request, new DefaultHttpContext(), "old");
        var current = webSockets ? ws.WaitForCompletionAsync(client, currentRequest, currentSocket, "current")
            : sse.WaitForCompletionAsync(client, currentRequest, new DefaultHttpContext(), "current");
        var other = webSockets ? ws.WaitForCompletionAsync(otherClient, request, otherSocket, "old")
            : sse.WaitForCompletionAsync(otherClient, request, new DefaultHttpContext(), "old");

        await (webSockets ? ws.CompleteConnectionAsync(client, "old") : sse.CompleteConnectionAsync(client, "old"));
        await old.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(current.IsCompleted);
        Assert.False(other.IsCompleted);
        await (webSockets ? ws.CompleteAsync(client, currentRequest) : sse.CompleteAsync(client, currentRequest));
        await current.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(other.IsCompleted);
        await (webSockets ? ws.CompleteAsync(otherClient) : sse.CompleteAsync(otherClient));
        await other.WaitAsync(TimeSpan.FromSeconds(1));
        if (webSockets) Assert.Null(ws.GetWebSocket(otherClient, request));
        else Assert.Null(sse.GetHttpContext(otherClient, request));
    }
}
