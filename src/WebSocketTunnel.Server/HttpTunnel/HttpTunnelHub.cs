using Microsoft.AspNetCore.SignalR;

namespace WebSocketTunnel.Server.HttpTunnel;

public class HttpTunnelHub(HttpTunnelStore tunnelStore) : Hub
{
    private readonly HttpTunnelStore _tunnelStore = tunnelStore;

    public override Task OnConnectedAsync()
    {
        var clientId = GetClientId(Context);

        _tunnelStore.Connections.AddOrUpdate(clientId, Context.ConnectionId, (key, oldValue) => Context.ConnectionId);

        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var clientId = GetClientId(Context);

        if (_tunnelStore.Clients.TryGetValue(clientId, out var subdomain))
        {
            _tunnelStore.Tunnels.Remove(subdomain, out var _);
            _tunnelStore.Connections.Remove(clientId, out var _);
            _tunnelStore.Clients.Remove(clientId, out _);
        }

        return base.OnDisconnectedAsync(exception);
    }

    private static Guid GetClientId(HubCallerContext context)
    {
        return Guid.Parse(context.GetHttpContext()!.Request.Query["clientId"].ToString());
    }
}
