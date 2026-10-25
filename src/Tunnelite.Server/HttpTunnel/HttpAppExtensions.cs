using System.Buffers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Tunnelite.Protocol;

namespace Tunnelite.Server.HttpTunnel;

public static class HttpAppExtensions
{
    private const int Status425TooEarly = 425;
    private static readonly string[] SupportedMethods = ["GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS", "HEAD"];

    public static void UseHttpTunneling(this WebApplication app)
    {
        app.MapPost("/tunnelite/tunnel", (HttpContext context, [FromBody] HttpTunnelRequest payload, HttpTunnelStore tunnelStore, ILogger<Program> logger) =>
            RegisterTunnelAsync(context, payload, tunnelStore, logger));

        app.MapGet("/tunnelite/request/{requestId}", (HttpContext context, [FromRoute] Guid requestId, HttpRequestsQueue requestsQueue, ILogger<Program> logger) =>
            GetRequestBodyAsync(context, requestId, requestsQueue, logger));

        app.MapPost("/tunnelite/request/{requestId}", (HttpContext context, [FromRoute] Guid requestId, HttpRequestsQueue requestsQueue, ILogger<Program> logger) =>
            ForwardResponseBodyAsync(app, context, requestId, requestsQueue, logger));

        app.MapDelete("/tunnelite/request/{requestId}", (HttpContext context, [FromRoute] Guid requestId, HttpRequestsQueue requestsQueue, ILogger<Program> logger) =>
            AbortRequestAsync(app, context, requestId, requestsQueue, logger));

        app.MapMethods(
            pattern: "/",
            httpMethods: SupportedMethods,
            handler: (HttpContext context, IHubContext<HttpTunnelHub> hubContext, HttpRequestsQueue requestsQueue, HttpTunnelStore connectionStore, ILogger<Program> logger) =>
                TunnelRequestAsync(app, context, hubContext, requestsQueue, connectionStore, path: string.Empty, logger));

        app.MapMethods(
            pattern: "/{**path}",
            httpMethods: SupportedMethods,
            handler: (HttpContext context, IHubContext<HttpTunnelHub> hubContext, HttpRequestsQueue requestsQueue, HttpTunnelStore connectionStore, string path, ILogger<Program> logger) =>
                TunnelRequestAsync(app, context, hubContext, requestsQueue, connectionStore, path, logger));

        app.MapHub<HttpTunnelHub>("/wsshttptunnel");
    }

    private static async Task RegisterTunnelAsync(HttpContext context, HttpTunnelRequest payload, HttpTunnelStore tunnelStore, ILogger<Program> logger)
    {
        try
        {
            if (!Uri.TryCreate(payload.LocalUrl, UriKind.Absolute, out var local) || local.Scheme is not ("http" or "https"))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Missing or invalid 'LocalUrl' property.");
                return;
            }

            if (payload.ClientId == Guid.Empty)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Missing or invalid 'ClientId' property.");
                return;
            }

            if (EndpointIdentity.IsProtected(payload.ClientId) || EndpointIdentity.IsProtected(payload.Subdomain ?? ""))
            {
                if (!EndpointIdentity.TryDerive(EndpointIdentityMiddleware.Bearer(context), out var ownerId, out var name)
                    || ownerId != payload.ClientId || !string.Equals(name, payload.Subdomain, StringComparison.OrdinalIgnoreCase)
                    || payload.ProtocolVersion < 2)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                payload.Subdomain = name;
                if (payload.RequirePairing) payload.ViewerKey = EndpointIdentity.ViewerKey(EndpointIdentityMiddleware.Bearer(context)!);
            }
            else if (payload.RequirePairing)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // Allow the hub's OnConnected callback to finish, without retaining registrations for absent clients.
            HttpTunnelSession? session = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (tunnelStore.Connections.TryGetValue(payload.ClientId, out session)
                    && (payload.ConnectionId is null || payload.ConnectionId == session.ConnectionId)) break;
                session = null;
                await Task.Delay(10, context.RequestAborted);
            }
            if (session is null) { context.Response.StatusCode = Status425TooEarly; return; }

            if (string.IsNullOrEmpty(payload.Subdomain))
            {
                do { payload.Subdomain = RandomSubdomain(); }
                while (ReservedSubdomains.Contains(payload.Subdomain) || tunnelStore.Tunnels.ContainsKey(payload.Subdomain));
            }
            else
            {
                payload.Subdomain = payload.Subdomain.ToLowerInvariant();
                if (payload.Subdomain.Length > 63 || payload.Subdomain.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
                    || payload.Subdomain.StartsWith('-') || payload.Subdomain.EndsWith('-'))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                if (ReservedSubdomains.Contains(payload.Subdomain))
                {
                    payload.Subdomain = RandomSubdomain();
                }

                // Don't hijack existing subdomain from another client.
                if (tunnelStore.Tunnels.TryGetValue(payload.Subdomain, out var tunnel) && tunnel.ClientId != payload.ClientId)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("Missing or invalid 'Subdomain' property.");
                    return;
                }
            }

            payload.LocalUrl = payload.LocalUrl.TrimEnd(['/']);
            payload.IsLoopback = HttpTunnelStore.IsLoopbackHost(context);

            var status = await session.RegisterAsync(payload, tunnelStore, context.RequestAborted);
            if (status != StatusCodes.Status201Created) { context.Response.StatusCode = status; return; }

            var tunnelUrl = payload.IsLoopback
                ? $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}"
                : $"{context.Request.Scheme}://{payload.Subdomain}.{context.Request.Host}{context.Request.PathBase}";

            context.Response.StatusCode = StatusCodes.Status201Created;
            await context.Response.WriteAsJsonAsync(new
            {
                TunnelUrl = tunnelUrl,
                payload.Subdomain,
                SupportsWebSocketClose = true,
                ProtectedEndpoint = EndpointIdentity.IsProtected(payload.ClientId),
                payload.RequirePairing,
            });
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating tunnel: {Message}", ex.Message);

            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new
            {
                Message = "An error occurred while creating the tunnel",
            });
        }
    }

    private static async Task GetRequestBodyAsync(HttpContext context, Guid requestId, HttpRequestsQueue requestsQueue, ILogger<Program> logger)
    {
        HttpContext? deferredHttpContext = null;
        try
        {
            deferredHttpContext = requestsQueue.GetHttpContext(requestId);
            if (deferredHttpContext == null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.Headers.Append("X-T-Method", deferredHttpContext.Request.Method);

            foreach (var header in deferredHttpContext.Request.Headers)
            {
                context.Response.Headers.Append($"X-TR-{header.Key}", header.Value.ToString());
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deferredHttpContext.RequestAborted);
            await deferredHttpContext.Request.Body.CopyToAsync(context.Response.Body, linked.Token);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested || deferredHttpContext?.RequestAborted.IsCancellationRequested == true)
        {
            context.Abort();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching request body: {Message}", ex.Message);

            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new
            {
                Message = "An error occurred while fetching the request body",
            });
        }
    }

    private static async Task ForwardResponseBodyAsync(WebApplication app, HttpContext context, Guid requestId, HttpRequestsQueue requestsQueue, ILogger<Program> logger)
    {
        HttpContext? deferredHttpContext = null;
        try
        {
            deferredHttpContext = requestsQueue.ClaimResponseHttpContext(requestId);
            if (deferredHttpContext == null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (context.Request.Headers.TryGetValue("X-T-Status", out var statusCodeHeader)
                && int.TryParse(statusCodeHeader, out var statusCode))
            {
                deferredHttpContext.Response.StatusCode = statusCode;
            }
            else
            {
                deferredHttpContext.Response.StatusCode = StatusCodes.Status200OK;
            }

            // Ordinal-insensitive on purpose: header names arrive lowercased from any client that
            // normalises them, which is every browser's fetch and every HTTP/2 client.
            foreach (var header in context.Request.Headers)
            {
                if (header.Key.StartsWith("X-TR-", StringComparison.OrdinalIgnoreCase)
                    || header.Key.StartsWith("X-TC-", StringComparison.OrdinalIgnoreCase))
                {
                    var headerKey = header.Key[5..];
                    if (!NotAllowedHeaders.Contains(headerKey))
                    {
                        deferredHttpContext.Response.Headers.TryAdd(headerKey, header.Value);
                    }
                }
            }

            // SSE is a long-lived, possibly idle response transported as this request body.
            // Change limits for this transfer only; ordinary requests retain Kestrel's limits.
            if (deferredHttpContext.Response.ContentType?.Split(';')[0].Trim()
                .Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (context.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } rate) rate.MinDataRate = null;
                if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
                    size.MaxRequestBodySize = null;
            }

            using var transferLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted,
                deferredHttpContext.RequestAborted);
            var buffer = ArrayPool<byte>.Shared.Rent(TunnelProtocol.ChunkSize);
            try
            {
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, TunnelProtocol.ChunkSize), transferLifetime.Token)) != 0)
                {
                    await deferredHttpContext.Response.Body.WriteAsync(buffer.AsMemory(0, read), transferLifetime.Token);
                    await deferredHttpContext.Response.Body.FlushAsync(transferLifetime.Token);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsJsonAsync(new { Message = "Ok" });
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested || deferredHttpContext?.RequestAborted.IsCancellationRequested == true)
        {
            deferredHttpContext?.Abort();
            context.Abort();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error forwarding response body: {Message}", ex.Message);
            if (deferredHttpContext is not null)
            {
                try
                {
                    if (deferredHttpContext.Response.HasStarted) deferredHttpContext.Abort();
                    else await ServerErrorAsync(app, deferredHttpContext);
                }
                catch (Exception failure) when (failure is IOException or OperationCanceledException)
                { deferredHttpContext.Abort(); }
            }

            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new
            {
                Message = "An error occurred while forwarding the response body",
            });
        }
        finally
        {
            if (deferredHttpContext is not null) await requestsQueue.CompleteAsync(requestId);
        }
    }

    private static async Task AbortRequestAsync(WebApplication app, HttpContext context, Guid requestId, HttpRequestsQueue requestsQueue, ILogger<Program> logger)
    {
        try
        {
            var deferredHttpContext = requestsQueue.ClaimResponseHttpContext(requestId);
            if (deferredHttpContext == null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (deferredHttpContext.Response.HasStarted) deferredHttpContext.Abort();
            else await ServerErrorAsync(app, deferredHttpContext);

            await requestsQueue.CompleteAsync(requestId);

            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsJsonAsync(new { Message = "Ok" });
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error forwarding response body: {Message}", ex.Message);

            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new
            {
                Message = "An error occurred while forwarding the response body",
            });
        }
    }

    static async Task TunnelRequestAsync(WebApplication app, HttpContext context, IHubContext<HttpTunnelHub> hubContext, HttpRequestsQueue requestsQueue, HttpTunnelStore tunnelStore, string path, ILogger<Program> logger)
    {
        var requestId = Guid.Empty;
        try
        {
            var host = context.Request.Host.Host;
            var subdomain = host.Split('.')[0];

            if (host.Length > 4 && host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                var apexHost = host[4..];
                var hostHeader = context.Request.Host.Port is int port
                    ? new HostString(apexHost, port)
                    : new HostString(apexHost);
                var targetUrl = $"{context.Request.Scheme}://{hostHeader}{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";

                context.Response.Redirect(targetUrl, permanent: true);
                return;
            }

            if (subdomain.Equals("tunnelite", StringComparison.OrdinalIgnoreCase))
            {
                await LandingSiteAsync(app, context, path);
                return;
            }

            if (!tunnelStore.TryResolveActiveTunnel(context, out var tunnel, out var connectionId))
            {
                await NotFoundAsync(app, context);
                return;
            }

            requestId = Guid.NewGuid();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, connectionId.Disconnected);

            var streaming = context.Request.Headers.Accept.Any(h => h?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true);
            var completionTask = requestsQueue.WaitForCompletionAsync(requestId, context, timeout: streaming ? null : TimeSpan.FromSeconds(30), lifetime.Token, tunnel.ClientId, connectionId.ConnectionId);

            await hubContext.Clients.Client(connectionId.ConnectionId).SendAsync("NewHttpConnection", new HttpConnection
            {
                RequestId = requestId,
                ContentType = context.Request.ContentType,
                Method = context.Request.Method,
                Path = $"{tunnel.LocalUrl}/{path}{context.Request.QueryString}",
            }, lifetime.Token);

            await completionTask;
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync("An error occurred while processing the tunnel.");
            }

            logger.LogError(ex, "Error processing request tunnel: {Message}", ex.Message);
        }
        finally { if (requestId != Guid.Empty) await requestsQueue.CompleteAsync(requestId); }
    }

    static readonly HashSet<string> ReservedSubdomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "tunnelite",
        "webhooks",
        "www",
    };

    // Files that belong to the landing site. They are only served on the root domain (tunnelite.com),
    // never on tunnel subdomains, so they can't shadow a tunneled application's own robots.txt, llms.txt, etc.
    static readonly Dictionary<string, string> LandingSiteFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["robots.txt"] = "text/plain; charset=utf-8",
        ["sitemap.xml"] = "application/xml; charset=utf-8",
        ["llms.txt"] = "text/plain; charset=utf-8",
        ["llms-full.txt"] = "text/plain; charset=utf-8",
        ["og-image.png"] = "image/png",
        ["apple-touch-icon.png"] = "image/png",
        ["favicon.ico"] = "image/x-icon",
        // previews of the error pages shown on tunnel subdomains
        ["404.html"] = "text/html; charset=utf-8",
        ["500.html"] = "text/html; charset=utf-8",
    };

    static async Task LandingSiteAsync(WebApplication app, HttpContext context, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            var filePath = Path.Combine(app.Environment.WebRootPath, "index.html");
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(filePath);
            return;
        }

        if (LandingSiteFiles.TryGetValue(path, out var contentType))
        {
            var sitePath = Path.Combine(app.Environment.WebRootPath, path.ToLowerInvariant());
            context.Response.ContentType = contentType;
            context.Response.Headers.CacheControl = "public, max-age=3600";
            await context.Response.SendFileAsync(sitePath);
            return;
        }

        await NotFoundAsync(app, context);
    }

    static async Task NotFoundAsync(WebApplication app, HttpContext context)
    {
        var filePath = Path.Combine(app.Environment.WebRootPath, "404.html");
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/html";
        await context.Response.SendFileAsync(filePath);
    }

    static async Task ServerErrorAsync(WebApplication app, HttpContext context)
    {
        var filePath = Path.Combine(app.Environment.WebRootPath, "500.html");
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        context.Response.ContentType = "text/html";
        await context.Response.SendFileAsync(filePath);
    }

    static string RandomSubdomain(int length = 8)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        return new(Enumerable.Range(0, length).Select(_ => chars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
    }

    // Case-insensitive for the same reason the prefixes above are: these names arrive lowercased from
    // any client that normalises them, and a hop-by-hop header that slips the filter lands on the
    // response.
    static readonly HashSet<string> NotAllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Connection", "Transfer-Encoding", "Keep-Alive", "Upgrade", "Proxy-Connection" };
}
