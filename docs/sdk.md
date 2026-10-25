# SDK integration

The host application supplies its mobile web UI and API. Bind that server to loopback and allow the public tunnel hostname in any host validation your application performs.

## Direct client

Load the same `TunnelIdentity.Secret` and `PairedAccessState` from desktop storage on each launch. Supply an asynchronous save callback to persist enrollment and revocation before acknowledging them.

```csharp
using Tunnelite.Sdk;

async Task RunRemoteAccessAsync(
    string savedSecret,
    PairedAccessState savedDevices,
    Func<PairedAccessState, CancellationToken, Task> saveDevices,
    Action<string> showQr,
    CancellationToken cancellationToken)
{
    var identity = new TunnelIdentity(savedSecret);
    using var access = new PairedAccess(identity, savedDevices, saveDevices);
    await using var client = new HttpTunnelClient(new HttpTunnelRequest
    {
        LocalUrl = "http://127.0.0.1:2584",
        PublicUrl = "https://tunnelite.com",
        Access = access,
    }, logLevel: null);

    client.Connected += () =>
    {
        showQr(client.CreatePairingUrl());
        return Task.CompletedTask;
    };
    await client.ConnectAsync(cancellationToken);
    await Task.Delay(Timeout.Infinite, cancellationToken);
}
```

For first-time setup, use `TunnelIdentity.Create()` and persist its secret before exposing the endpoint. Keep `PairedAccess` alive until the client is disposed. `Connected` fires after registration and reconnects, when `TunnelUrl` is ready. Concurrent startup calls share one registration attempt.

`access.State.Devices` lists device IDs, names, and expiry times. `await access.RevokeAsync(deviceId)` removes a device and cancels its active streams. Saved device records contain token hashes, not browser tokens. Creating a new pairing URL replaces any unused previous challenge.

## ASP.NET Core host

Register the hosted integration before building the app:

```csharp
builder.Services.AddTunnelite(options =>
{
    options.Access = access;
    options.PublicUrl = "https://tunnelite.com";
    options.OnReady = (client, cancellationToken) =>
    {
        ShowPairingQr(client.CreatePairingUrl());
        return Task.CompletedTask;
    };
});
```

`AddTunnelite` is in `Tunnelite.AspNetCore`. The host starts the tunnel after its local HTTP server is listening and disposes it during shutdown. `LocalUrl` can be set explicitly; wildcard bind addresses discovered from the host are forwarded through loopback. Existing `UseTunnelite()` calls do not start a second tunnel when the hosted service is configured.

## Local HTTPS

`HttpTunnelRequest.AllowUntrustedLocalCertificates` and the matching hosted option are nullable. `null` preserves ordinary HTTP tunnel behavior and verifies certificates in paired mode. `false` explicitly requires validation. `true` permits an untrusted certificate only on loopback. Relay TLS validation is always enabled.
