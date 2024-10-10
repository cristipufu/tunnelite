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

                client.Log += x => LogInfo("Tunnelite", x);
                client.LogRequest += (method, path) => LogInfo("Tunnelite", $"[{method}]: {path}");
                client.LogFailedRequest += (method, path) => LogError("Tunnelite", $"[{method}]: {path}");
                client.LogError += x => LogError("Tunnelite", x);
                client.LogException += x => LogError("Tunnelite", x.Message);

                await client.ConnectAsync();

                LogInfo("Tunnelite", client.TunnelUrl);

            }, CancellationToken.None);

        });

        return app;
    }

    private static void LogInfo(string category, string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("info: ");
        Console.ResetColor();
        Console.WriteLine($"{category}");
        Console.WriteLine($"      {message}");
    }

    private static void LogError(string category, string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("error: ");
        Console.ResetColor();
        Console.WriteLine($"{category}");
        Console.WriteLine($"      {message}");
    }
}
