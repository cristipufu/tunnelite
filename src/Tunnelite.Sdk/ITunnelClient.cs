using Microsoft.AspNetCore.SignalR.Client;

namespace Tunnelite.Sdk;

public interface ITunnelClient
{
    event Func<Task>? Connected;
    event Action<string, string>? LogRequest;
    event Action<string, string>? LogFailedRequest;
    event Action<Exception>? LogException;
    event Action<string>? Log;
    event Action<string>? LogError;

    HubConnection Connection { get; }

    string? TunnelUrl { get; }

    Task ConnectAsync();
}
