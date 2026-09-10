using Tunnelite.Server.HttpTunnel;
using Tunnelite.Server.SseTunnel;
using Tunnelite.Server.TcpTunnel;
using Tunnelite.Server.WsTunnel;

namespace Tunnelite.Server;

public static class Extensions
{
    public static void AddHttpTunneling(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<HttpTunnelStore>();
        builder.Services.AddSingleton<HttpRequestsQueue>();
        builder.Services.AddSingleton<SseRequestsQueue>();
        builder.Services.AddSingleton<WsRequestsQueue>();
    }

    public static void AddTcpTunneling(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<TcpTunnelStore>();
        builder.Services.AddSingleton<TcpClientStore>();
    }

    public static void ConfigureSignalR(this WebApplicationBuilder builder)
    {
        var signalRConnectionString = builder.Configuration.GetConnectionString("AzureSignalR");

        var signalRBuilder = builder.Services.AddSignalR(hubOptions =>
        {
            hubOptions.EnableDetailedErrors = true;

            // Stream items carry up to TunnelProtocol.ChunkSize bytes of payload (older clients send 32 KB), plus
            // the MessagePack envelope. The SignalR default of 32 KB rejected those messages and tore the whole hub
            // connection down, which surfaced as WebSocket resets for any message larger than the chunk size.
            hubOptions.MaximumReceiveMessageSize = 256 * 1024;
        }).AddMessagePackProtocol();

        if (!string.IsNullOrEmpty(signalRConnectionString))
        {
            signalRBuilder.AddAzureSignalR(opt =>
            {
                opt.ConnectionString = signalRConnectionString;
            });
        }
    }

    public static void UseFavicon(this WebApplication app)
    {
        app.MapGet("/favicon.ico", async context =>
        {
            context.Response.ContentType = "image/x-icon";
            await context.Response.SendFileAsync("wwwroot/favicon.ico");
        });
    }
}
