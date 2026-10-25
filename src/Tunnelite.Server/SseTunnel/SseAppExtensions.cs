namespace Tunnelite.Server.SseTunnel;

public static class SseAppExtensions
{
    public static void UseSseTunneling(this WebApplication app)
    {
        app.UseMiddleware<SseTunnelMiddleware>();
    }
}
