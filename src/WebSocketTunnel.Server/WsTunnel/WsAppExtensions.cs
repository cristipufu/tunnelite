namespace WebSocketTunnel.Server.WsTunnel;

public static class WsAppExtensions
{
    public static void UseWsTunneling(this WebApplication app)
    {
        app.UseWebSockets();

        app.UseMiddleware<WsTunnelMiddleware>();
    }
}
