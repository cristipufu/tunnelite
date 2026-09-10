using Tunnelite.Server.HttpTunnel;
using Tunnelite.Server.SseTunnel;
using Tunnelite.Server.TcpTunnel;
using Tunnelite.Server.WsTunnel;

namespace Tunnelite.Server;

/// <summary>
/// Builds the tunnel server. <c>Program.cs</c> runs it; the integration tests host it in-process.
/// </summary>
public static class TunneliteServer
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.AddHttpTunneling();

        builder.AddTcpTunneling();

        builder.ConfigureSignalR();

        var app = builder.Build();

        app.UseStaticFiles();

        app.UseFavicon();

        app.UseHttpsRedirection();

        app.UseWsTunneling();

        app.UsSseTunneling();

        app.UseHttpTunneling();

        app.UseTcpTunneling();

        return app;
    }
}
