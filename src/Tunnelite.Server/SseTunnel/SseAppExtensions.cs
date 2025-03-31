namespace Tunnelite.Server.SseTunnel;

public static class SseAppExtensions
{
    public static void UsSseTunneling(this WebApplication app)
    {
        app.UseMiddleware<SseTunnelMiddleware>();
    }
}
