using Tunnelite.Server.HttpTunnel;
using Tunnelite.Server.TcpTunnel;
using Tunnelite.Server.WsTunnel;

namespace Tunnelite.Server;

public static class Extensions
{
    public static void AddHttpTunneling(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<HttpTunnelStore>();
        builder.Services.AddSingleton<HttpRequestsQueue>();
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
