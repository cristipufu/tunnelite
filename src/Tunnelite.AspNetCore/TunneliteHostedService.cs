using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tunnelite.Sdk;

namespace Tunnelite.AspNetCore;

internal sealed class TunneliteHostedService(IServer server, IHostApplicationLifetime lifetime,
    TunneliteOptions options, ILogger<TunneliteHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ready = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);
        var address = options.LocalUrl ?? server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Unable to determine the local application URL.");
        var uri = new UriBuilder(address);
        if (uri.Host is "0.0.0.0" or "[::]" or "::") uri.Host = "127.0.0.1";
        await using var client = new HttpTunnelClient(new HttpTunnelRequest
        {
            ClientId = Guid.NewGuid(), PublicUrl = options.PublicUrl, LocalUrl = uri.Uri.ToString().TrimEnd('/'),
            Access = options.Access!, OwnerToken = options.Identity?.Secret!,
            AllowUntrustedLocalCertificates = options.AllowUntrustedLocalCertificates,
        }, null);
        client.LogException += exception => logger.LogError(exception, "Tunnel connection failed.");
        client.Connected += async () =>
        {
            logger.LogInformation("Tunnel ready at {TunnelUrl}", client.TunnelUrl);
            if (options.OnReady is { } callback) await callback(client, stoppingToken);
        };
        try
        {
            await client.ConnectAsync(stoppingToken);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
