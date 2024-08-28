﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Tunnelite.Server.HttpTunnel;

public static class HttpAppExtensions
{
    public static void UseHttpTunneling(this WebApplication app)
    {
        app.MapPost("/tunnelite/tunnel", async (HttpContext context, [FromBody] HttpTunnelRequest payload, HttpTunnelStore tunnelStore, ILogger<Program> logger) =>
        {
            try
            {
                if (string.IsNullOrEmpty(payload.LocalUrl))
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

                if (string.IsNullOrEmpty(payload.Subdomain))
                {
                    payload.Subdomain = RandomSubdomain();
                }
                else
                {
                    // reserved
                    if (payload.Subdomain.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                        payload.Subdomain.Equals("tunnelite", StringComparison.OrdinalIgnoreCase) ||
                        payload.Subdomain.Equals("webhooks", StringComparison.OrdinalIgnoreCase))
                    {
                        payload.Subdomain = RandomSubdomain();
                    }

                    // don't hijack existing subdomain from another client
                    if (tunnelStore.Tunnels.TryGetValue(payload.Subdomain, out var tunnel))
                    {
                        if (tunnel.ClientId != payload.ClientId)
                        {
                            context.Response.StatusCode = StatusCodes.Status400BadRequest;
                            await context.Response.WriteAsync("Missing or invalid 'Subdomain' property.");
                            return;
                        }
                    }
                }

                payload.LocalUrl = payload.LocalUrl.TrimEnd(['/']);

                tunnelStore.Tunnels.AddOrUpdate(payload.Subdomain, payload, (key, oldValue) => payload);
                tunnelStore.Clients.AddOrUpdate(payload.ClientId, payload.Subdomain, (key, oldValue) => payload.Subdomain);

                var subdomain = context.Request.Host.Host.Split('.')[0];

                var tunnelUrl = string.Empty;

                if (subdomain.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    tunnelUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
                }
                else
                {
                    tunnelUrl = $"{context.Request.Scheme}://{payload.Subdomain}.{context.Request.Host}{context.Request.PathBase}";
                }

                context.Response.StatusCode = StatusCodes.Status201Created;
                await context.Response.WriteAsJsonAsync(new
                {
                    TunnelUrl = tunnelUrl,
                    payload.Subdomain,
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error creating tunnel: {Message}", ex.Message);

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new
                {
                    Message = "An error occurred while creating the tunnel",
                    Error = ex.Message,
                });
            }
        });

        app.MapGet("/tunnelite/request/{requestId}", async (HttpContext context, [FromRoute] Guid requestId, HttpRequestsQueue requestsQueue, ILogger<Program> logger) =>
        {
            try
            {
                var deferredHttpContext = requestsQueue.GetHttpContext(requestId);

                if (deferredHttpContext == null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                // Send method
                context.Response.Headers.Append("X-T-Method", deferredHttpContext.Request.Method);

                // Send headers
                foreach (var header in deferredHttpContext.Request.Headers)
                {
                    context.Response.Headers.Append($"X-TR-{header.Key}", header.Value.ToString());
                }

                // Stream the body
                await deferredHttpContext.Request.Body.CopyToAsync(context.Response.Body);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error fetching request body: {Message}", ex.Message);

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new
                {
                    Message = "An error occurred while fetching the request body",
                    Error = ex.Message,
                });
            }
        });

        app.MapPost("/tunnelite/request/{requestId}", async (HttpContext context, [FromRoute] Guid requestId, HttpRequestsQueue requestsQueue, ILogger<Program> logger) =>
        {
            try
            {
                var deferredHttpContext = requestsQueue.GetHttpContext(requestId);

                if (deferredHttpContext == null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                // Set the status code
                if (context.Request.Headers.TryGetValue("X-T-Status", out var statusCodeHeader)
                    && int.TryParse(statusCodeHeader, out var statusCode))
                {
                    deferredHttpContext.Response.StatusCode = statusCode;
                }
                else
                {
                    deferredHttpContext.Response.StatusCode = 200; // Default to 200 OK if not specified
                }

                // Copy headers from the tunneling client's request to the deferred response

                foreach (var header in context.Request.Headers)
                {
                    if (header.Key.StartsWith("X-TR-"))
                    {
                        var headerKey = header.Key[5..]; // Remove "X-TR-" prefix

                        if (!NotAllowedHeaders.Contains(headerKey))
                        {
                            deferredHttpContext.Response.Headers.TryAdd(headerKey, header.Value);
                        }
                    }

                    if (header.Key.StartsWith("X-TC-"))
                    {
                        var headerKey = header.Key[5..]; // Remove "X-TR-" prefix

                        if (!NotAllowedHeaders.Contains(headerKey))
                        {
                            deferredHttpContext.Response.Headers.TryAdd(headerKey, header.Value);
                        }
                    }
                }

                // Stream the body from the tunneling client's request to the deferred response
                await context.Request.Body.CopyToAsync(deferredHttpContext.Response.Body);

                // Complete the deferred response
                await requestsQueue.CompleteAsync(requestId);

                // Send a confirmation response to the tunneling client
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteAsJsonAsync(new { Message = "Ok" });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error forwarding response body: {Message}", ex.Message);

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new
                {
                    Message = "An error occurred while forwarding the response body",
                    Error = ex.Message,
                });
            }
        });

        app.MapDelete("/tunnelite/request/{requestId}", async (HttpContext context, [FromRoute] Guid requestId, HttpRequestsQueue requestsQueue, ILogger<Program> logger) =>
        {
            try
            {
                var deferredHttpContext = requestsQueue.GetHttpContext(requestId);

                if (deferredHttpContext == null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await ServerErrorAsync(app, deferredHttpContext);

                // Complete the deferred response
                await requestsQueue.CompleteAsync(requestId);

                // Send a confirmation response to the tunneling client
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteAsJsonAsync(new { Message = "Ok" });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error forwarding response body: {Message}", ex.Message);

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new
                {
                    Message = "An error occurred while forwarding the response body",
                    Error = ex.Message,
                });
            }
        });

        var supportedMethods = new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS", "HEAD" };

        app.MapMethods(
            pattern: "/",
            httpMethods: supportedMethods,
            handler: (HttpContext context, IHubContext<HttpTunnelHub> hubContext, HttpRequestsQueue requestsQueue, HttpTunnelStore connectionStore, ILogger<Program> logger) =>
                TunnelRequestAsync(app, context, hubContext, requestsQueue, connectionStore, path: string.Empty, logger));

        app.MapMethods(
            pattern: "/{**path}",
            httpMethods: supportedMethods,
            handler: (HttpContext context, IHubContext<HttpTunnelHub> hubContext, HttpRequestsQueue requestsQueue, HttpTunnelStore connectionStore, string path, ILogger<Program> logger) =>
                TunnelRequestAsync(app, context, hubContext, requestsQueue, connectionStore, path, logger));
    
        app.MapHub<HttpTunnelHub>("/wsshttptunnel");
    }

    static async Task TunnelRequestAsync(WebApplication app, HttpContext context, IHubContext<HttpTunnelHub> hubContext, HttpRequestsQueue requestsQueue, HttpTunnelStore tunnelStore, string path, ILogger<Program> logger)
    {
        try
        {
            HttpTunnelRequest? tunnel = null;

            var subdomain = context.Request.Host.Host.Split('.')[0];

            if (subdomain.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                tunnel = tunnelStore.Tunnels.FirstOrDefault().Value;
            }
            else if (subdomain.Equals("tunnelite"))
            {
                var filePath = Path.Combine(app.Environment.WebRootPath, "index.html");
                context.Response.ContentType = "text/html";
                await context.Response.SendFileAsync(filePath);
               
                return;
            }
            else
            {
                tunnelStore.Tunnels.TryGetValue(subdomain, out tunnel);
            }

            if (tunnel == null)
            {
                await NotFoundAsync(app, context);
                return;
            }

            if (!tunnelStore.Connections.TryGetValue(tunnel!.ClientId, out var connectionId))
            {
                await NotFoundAsync(app, context);
                return;
            }

            var requestId = Guid.NewGuid();

            var completionTask = requestsQueue.WaitForCompletionAsync(requestId, context, timeout: TimeSpan.FromSeconds(30), context.RequestAborted);

            await hubContext.Clients.Client(connectionId).SendAsync("NewHttpConnection", new HttpConnection
            {
                RequestId = requestId,
                ContentType = context.Request.ContentType,
                Method = context.Request.Method,
                Path = $"{tunnel.LocalUrl}/{path}{context.Request.QueryString}",
            });

            await completionTask;
        }
        catch (TaskCanceledException)
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
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/html";
        await context.Response.SendFileAsync(filePath);
    }

    static string RandomSubdomain(int length = 8)
    {
        Random random = new();
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        return new(Enumerable.Repeat(chars, length)
            .Select(s => s[random.Next(s.Length)]).ToArray());
    }

    static readonly string[] NotAllowedHeaders = ["Connection", "Transfer-Encoding", "Keep-Alive", "Upgrade", "Proxy-Connection"];
}