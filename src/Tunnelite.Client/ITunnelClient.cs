using Microsoft.AspNetCore.SignalR.Client;

namespace Tunnelite.Client;

public interface ITunnelClient
{
    event Func<Task>? Connected;

    HubConnection Connection { get; }

    string? TunnelUrl { get; }

    Task ConnectAsync();
}
