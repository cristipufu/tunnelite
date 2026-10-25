using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Tunnelite.Server.HttpTunnel;

public class HttpTunnelStore
{
    // subdomain, [clientId, localUrl]
    public ConcurrentDictionary<string, HttpTunnelRequest> Tunnels { get; } = new(StringComparer.OrdinalIgnoreCase);

    // clientId, connectionId
    public ConcurrentDictionary<Guid, HttpTunnelSession> Connections { get; } = new();

    /// <summary>
    /// True only when the request targets a bare <c>localhost</c> host header over a loopback connection.
    /// Prevents <c>localhost.tunnelite.com</c> or spoofed remote <c>Host: localhost</c> headers from
    /// matching arbitrary customer tunnels.
    /// </summary>
    public static bool IsLoopbackHost(HttpContext context) =>
        context.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        && (context.Connection.RemoteIpAddress is null || IPAddress.IsLoopback(context.Connection.RemoteIpAddress));

    public HttpTunnelRequest? ResolveTunnel(HttpContext context)
    {
        if (IsLoopbackHost(context))
            return Tunnels.Values.FirstOrDefault(t => Connections.ContainsKey(t.ClientId));

        var host = context.Request.Host.Host;
        var dot = host.IndexOf('.');
        if (dot <= 0) return null;

        var subdomain = host[..dot];
        if (subdomain.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || subdomain.Equals("tunnelite", StringComparison.OrdinalIgnoreCase)
            || subdomain.Equals("www", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Tunnels.GetValueOrDefault(subdomain);
    }

    public bool TryResolveActiveTunnel(
        HttpContext context,
        [NotNullWhen(true)] out HttpTunnelRequest? tunnel,
        [NotNullWhen(true)] out HttpTunnelSession? session)
    {
        tunnel = ResolveTunnel(context);
        if (tunnel is not null && Connections.TryGetValue(tunnel.ClientId, out session))
        {
            return true;
        }

        session = null;
        return false;
    }
}

/// <summary>Owns one connection's registration. Only registration and teardown share its asynchronous gate.</summary>
public sealed class HttpTunnelSession(string connectionId, CancellationToken disconnected)
{
    private readonly SemaphoreSlim _registration = new(1, 1);
    private HttpTunnelRequest? _tunnel;
    private bool _closed;
    public string ConnectionId { get; } = connectionId;
    public CancellationToken Disconnected { get; } = disconnected;

    internal async Task<int> RegisterAsync(HttpTunnelRequest payload, HttpTunnelStore store, CancellationToken cancellationToken)
    {
        await _registration.WaitAsync(cancellationToken);
        try
        {
            if (_closed || Disconnected.IsCancellationRequested
                || (payload.ConnectionId is not null && payload.ConnectionId != ConnectionId)
                || !ReferenceEquals(store.Connections.GetValueOrDefault(payload.ClientId), this)) return 425;
            if (_tunnel is not null && _tunnel.Subdomain != payload.Subdomain) return 409;
            var registered = store.Tunnels.AddOrUpdate(payload.Subdomain!, payload, (_, previous) =>
                previous.ClientId == payload.ClientId && ReferenceEquals(store.Connections.GetValueOrDefault(payload.ClientId), this) ? payload : previous);
            if (!ReferenceEquals(registered, payload))
                return ReferenceEquals(store.Connections.GetValueOrDefault(payload.ClientId), this) ? 409 : 425;
            if (!ReferenceEquals(store.Connections.GetValueOrDefault(payload.ClientId), this))
            {
                store.Tunnels.TryRemove(new KeyValuePair<string, HttpTunnelRequest>(payload.Subdomain!, payload));
                return 425;
            }
            _tunnel = payload;
            return 201;
        }
        finally { _registration.Release(); }
    }

    internal async Task CloseAsync(HttpTunnelStore store)
    {
        await _registration.WaitAsync();
        try
        {
            _closed = true;
            if (_tunnel is not null) store.Tunnels.TryRemove(new KeyValuePair<string, HttpTunnelRequest>(_tunnel.Subdomain!, _tunnel));
        }
        finally { _registration.Release(); }
    }
}

public class HttpTunnelRequest
{
    public string? ConnectionId { get; set; }
    public bool RequirePairing { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[]? ViewerKey { get; set; }
    public int ProtocolVersion { get; set; }
    public string? Subdomain { get; set; }

    public Guid ClientId { get; set; }

    public string? LocalUrl { get; set; }
}
