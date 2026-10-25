using Tunnelite.Protocol;

namespace Tunnelite.Server.HttpTunnel;

/// <summary>Rejects anonymous visitors before forwarding requests or upgrading WebSockets.</summary>
public sealed class PairedEndpointMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, HttpTunnelStore tunnels)
    {
        var path = context.Request.Path.Value ?? "";
        if (path.StartsWith("/tunnelite/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/wsshttptunnel", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/wsstcptunnel", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }
        var tunnel = tunnels.ResolveTunnel(context);
        if (tunnel?.RequirePairing != true) { await next(context); return; }
        var bootstrap = path is "/_tunnelite/pair" or "/_tunnelite/pair.js";
        var valid = tunnel.ViewerKey is { } key && ViewerTicket.Validate(key,
            context.Request.Cookies[ViewerTicket.CookieName(context.Request.IsHttps)], DateTimeOffset.UtcNow, out _, out _);
        var origin = context.Request.Headers.Origin.ToString();
        var expectedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        if ((!bootstrap && !valid) || (origin.Length > 0 && !string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = 401;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync("Scan the pairing QR code on your desktop to connect.");
            return;
        }
        await next(context);
    }
}
