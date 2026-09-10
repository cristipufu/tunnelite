using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Tunnelite.Sdk;
using Tunnelite.Server;

namespace Tunnelite.Tests.Infrastructure;

/// <summary>
/// One tunnel server, one <see cref="LocalApp"/>, and SDK clients tunnelling between them - all in-process.
/// Each test class gets its own instance (xUnit class fixture) because the server routes requests on the
/// <c>localhost</c> host to the first registered HTTP tunnel.
/// </summary>
public class TunnelHost : IAsyncLifetime
{
    private WebApplication? _server;

    public string PublicUrl { get; private set; } = "";
    public LocalApp App { get; private set; } = null!;
    public HttpTunnelClient? HttpTunnel { get; private set; }
    public TcpTunnelClient? TcpTunnel { get; private set; }

    /// <summary>Public TCP port assigned by the server when <see cref="StartTcpTunnel"/> is on.</summary>
    public int PublicTcpPort { get; private set; }

    /// <summary>Everything the SDK reported through LogFailedRequest, LogError and LogException.</summary>
    public ConcurrentQueue<string> ClientFailures { get; } = new();

    protected virtual bool StartHttpTunnel => true;
    protected virtual bool StartTcpTunnel => false;

    public async Task InitializeAsync()
    {
        _server = TunneliteServer.Build([
            "--urls", "http://127.0.0.1:0",
            "--contentRoot", AppContext.BaseDirectory,
            "--Logging:LogLevel:Default=Warning",
        ]);
        await _server.StartAsync();

        // The server treats the "localhost" host name as "the first registered tunnel".
        PublicUrl = $"http://localhost:{new Uri(_server.Urls.First()).Port}";

        App = await LocalApp.StartAsync();

        if (StartHttpTunnel)
        {
            HttpTunnel = new HttpTunnelClient(new HttpTunnelRequest
            {
                ClientId = Guid.NewGuid(),
                LocalUrl = App.Url,
                PublicUrl = PublicUrl,
            }, logLevel: null);
            Capture(HttpTunnel);

            await HttpTunnel.ConnectAsync().WaitAsync(TestData.Timeout);

            Assert.Equal(PublicUrl, HttpTunnel.TunnelUrl);
        }

        if (StartTcpTunnel)
        {
            TcpTunnel = new TcpTunnelClient(new TcpTunnelRequest
            {
                ClientId = Guid.NewGuid(),
                LocalUrl = $"tcp://localhost:{App.TcpPort}",
                Host = "localhost",
                LocalPort = App.TcpPort,
                PublicUrl = PublicUrl,
            }, logLevel: null);
            Capture(TcpTunnel);

            await TcpTunnel.ConnectAsync().WaitAsync(TestData.Timeout);

            Assert.NotNull(TcpTunnel.TunnelUrl);
            PublicTcpPort = new Uri(TcpTunnel.TunnelUrl).Port;
        }
    }

    private void Capture(ITunnelClient client)
    {
        client.LogFailedRequest += (method, path) => ClientFailures.Enqueue($"failed request: {method} {path}");
        client.LogError += message => ClientFailures.Enqueue($"error: {message}");
        client.LogException += exception => ClientFailures.Enqueue($"exception: {exception.GetType().Name}: {exception.Message}");
    }

    public async Task DisposeAsync()
    {
        if (HttpTunnel != null)
        {
            await HttpTunnel.DisposeAsync();
        }

        if (TcpTunnel != null)
        {
            await TcpTunnel.DisposeAsync();
        }

        await App.DisposeAsync();

        if (_server != null)
        {
            await _server.StopAsync();
            await _server.DisposeAsync();
        }
    }
}

/// <summary>Server plus local app, no SDK tunnel: the test brings its own client (the CLI).</summary>
public sealed class BareTunnelHost : TunnelHost
{
    protected override bool StartHttpTunnel => false;
}

public sealed class TcpTunnelHost : TunnelHost
{
    protected override bool StartHttpTunnel => false;
    protected override bool StartTcpTunnel => true;
}
