using Tunnelite.Protocol;

namespace Tunnelite.Server.HttpTunnel;

/// <summary>Verifies protected desktop identities without persistent server state.</summary>
public sealed class EndpointIdentityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, HttpRequestsQueue requests)
    {
        Guid clientId = default;
        var path = context.Request.Path.Value ?? "";
        if (path.StartsWith("/wsshttptunnel", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/wsstcptunnel", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(context.Request.Query["clientId"], out clientId) || clientId == Guid.Empty)
            {
                context.Response.StatusCode = 400;
                return;
            }
        }
        else if (path.StartsWith("/tunnelite/request/", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(path["/tunnelite/request/".Length..], out var requestId))
        {
            clientId = requests.GetClientId(requestId);
        }

        if (EndpointIdentity.IsProtected(clientId)
            && (!EndpointIdentity.TryDerive(Bearer(context), out var ownerId, out _) || ownerId != clientId))
        {
            context.Response.StatusCode = 401;
            return;
        }
        await next(context);
    }

    internal static string? Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
    }
}
