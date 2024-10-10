using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tunnelite.Sdk;

#nullable disable
namespace Tunnelite.AspNetCore;

public static class TunneliteExtensions
{
    private static readonly Guid ClientId = Guid.NewGuid();

    public static IApplicationBuilder UseTunnelite(this IApplicationBuilder app)
    {
        var lifetime = app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>();
        var server = app.ApplicationServices.GetRequiredService<IServer>();

        lifetime.ApplicationStarted.Register(() =>
        {
            var addressFeature = server.Features.Get<IServerAddressesFeature>();
            var localUrl = addressFeature?.Addresses.FirstOrDefault();

            if (string.IsNullOrEmpty(localUrl))
            {
                throw new InvalidOperationException("Unable to determine the local URL of the application.");
            }

            Task.Run(async () =>
            {
                var httpTunnel = new HttpTunnelRequest
                {
                    ClientId = ClientId,
                    LocalUrl = localUrl,
                    PublicUrl = "https://tunnelite.com",
                };

                var client = new HttpTunnelClient(httpTunnel, null);

                client.Log += x => Console.WriteLine(x);
                client.LogRequest += (method, path) => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} [{method}]: {path}");
                client.LogFailedRequest += (method, path) => Console.Write($"{DateTimeOffset.Now:HH:mm:ss} [{method}]: {path}");
                client.LogError += x => Console.WriteLine(x);
                client.LogException += x => Console.WriteLine(x.Message);

                await client.ConnectAsync();

                LogTunnelInfo(client.TunnelUrl);

            }, CancellationToken.None);

        });

        return app;
    }

    private static void LogTunnelInfo(string tunnelUrl)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine($"   Tunnelite URL: {tunnelUrl,-67}");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
    }
}
