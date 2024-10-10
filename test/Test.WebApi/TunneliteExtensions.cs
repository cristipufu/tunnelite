using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Spectre.Console;
using Tunnelite.Client.HttpTunnel;

namespace Test.WebApi
{
    public static class TunneliteExtensions
    {
        private static readonly Guid ClientId = Guid.NewGuid();

        public static IApplicationBuilder UseTunnelite(this IApplicationBuilder app)
        {
            var lifetime = app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>();
            var server = app.ApplicationServices.GetRequiredService<IServer>();
            var logger = app.ApplicationServices.GetRequiredService<ILogger<IApplicationBuilder>>();

            lifetime.ApplicationStarted.Register(() =>
            {
                var addressFeature = server.Features.Get<IServerAddressesFeature>();
                var localUrl = addressFeature?.Addresses.FirstOrDefault();

                if (string.IsNullOrEmpty(localUrl))
                {
                    throw new InvalidOperationException("Unable to determine the local URL of the application.");
                }

                var httpTunnel = new HttpTunnelRequest
                {
                    ClientId = ClientId,
                    LocalUrl = localUrl,
                    PublicUrl = "https://tunnelite.com",
                };

                var client = new HttpTunnelClient(httpTunnel, null);

                client.ConnectAsync().GetAwaiter().GetResult();

                Table statusTable = Tunnelite.Client.Program.WriteStatusTable(localUrl, client.TunnelUrl, "green", "Connected");

            });

            return app;
        }
    }
}
