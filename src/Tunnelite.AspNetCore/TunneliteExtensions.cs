using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tunnelite.Sdk;

#nullable disable
namespace Tunnelite.AspNetCore;

public static class TunneliteExtensions
{
    /// <summary>Adds a background service that starts after the local server and closes the tunnel during host shutdown.</summary>
    public static IServiceCollection AddTunnelite(this IServiceCollection services, Action<TunneliteOptions> configure = null)
    {
        var options = new TunneliteOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddHostedService<TunneliteHostedService>();
        return services;
    }

    public static IApplicationBuilder UseTunnelite(this IApplicationBuilder app)
    {
        if (app.ApplicationServices.GetService<TunneliteOptions>() is not null) return app;
        var lifetime = app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>();
        var server = app.ApplicationServices.GetRequiredService<IServer>();
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("Tunnelite");

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
                    ClientId = Guid.NewGuid(),
                    LocalUrl = localUrl,
                    PublicUrl = "https://tunnelite.com",
                };

                await using var client = new HttpTunnelClient(httpTunnel, null);

                client.Log += message => logger.LogInformation("{Message}", message);
                client.LogRequest += (method, path) => logger.LogInformation("[{Method}]: {Path}", method, path);
                client.LogFailedRequest += (method, path) => logger.LogError("[{Method}]: {Path}", method, path);
                client.LogError += message => logger.LogError("{Message}", message);
                client.LogException += exception => logger.LogError(exception, "{Message}", exception.Message);

                try
                {
                    await client.ConnectAsync(lifetime.ApplicationStopping);
                    logger.LogInformation("Tunnel ready at {TunnelUrl}", client.TunnelUrl);
                    await Task.Delay(Timeout.Infinite, lifetime.ApplicationStopping);
                }
                catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested) { }
                catch (Exception exception) { logger.LogError(exception, "{Message}", exception.Message); }

            }, CancellationToken.None);

        });

        return app;
    }
}
