using Microsoft.Net.Http.Headers;
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
            context.Response.Headers.Vary = "Accept";
            context.Response.Headers.ContentSecurityPolicy = PairingPage.ContentSecurityPolicy;
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (WantsHtml(context.Request))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(PairingPage.Required, context.RequestAborted);
            }
            else
            {
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("Scan the pairing QR code on your desktop to connect.", context.RequestAborted);
            }
            return;
        }
        await next(context);
    }

    private static bool WantsHtml(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) || request.Headers.ContainsKey("Upgrade")
            || !MediaTypeHeaderValue.TryParseList(request.Headers.Accept, out var accepted)) return false;
        var html = false;
        foreach (var media in accepted)
        {
            if (media.Quality == 0) continue;
            if (media.MediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase)) return false;
            if (media.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)) html = true;
        }
        return html;
    }
}
