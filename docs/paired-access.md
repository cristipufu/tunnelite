# Paired access

For a quick mobile preview with no access restriction, use `tunnelite http://localhost:3000`. A QR code appears automatically. Scan it to open the public URL on your phone.

To restrict access to browsers you authorize, start a paired tunnel to a website already running on your computer:

```sh
tunnelite serve http://localhost:3000
```

Scan the QR with the phone's camera. The browser enrolls once and opens the app. No phone-side VPN or Tunnelite client is required. Each code enrolls one browser and expires after five minutes; browser sessions expire after 30 days.

`tunnelite http://localhost:3000 --pair` is an alternative syntax for the same paired mode. Use `serve` when following the examples here.

## Devices

While the CLI runs:

- `p`: replace the pairing code and show a new QR.
- `d`: open the device panel; use Up/Down to scroll and Esc to return.
- `r`: select a device with Up/Down and press Enter to revoke it. Esc cancels. Its active streams close too.
- `c`: reset the activity counters and clear recent errors.
- `q`: close the tunnel and exit.

Redirected stdin accepts one command per line: `pair`, `devices`, `revoke <device-id>`, and `q`.

## Desktop profiles

By default, paired mode uses a saved setup called `default`. `--name` is an optional label for a separate saved setup on your computer; it does not name your website or choose a public hostname. For example:

```sh
tunnelite serve http://localhost:3000 --name my-website
```

Use a different profile for each app you expose concurrently. Reusing a profile keeps its address and paired devices even if the local port changes.

Profiles live in the desktop user's application-data directory under `tunnelite`. Use `--identity-file <path>` for an explicit location. A profile contains the desktop secret and device token hashes. One CLI process can open a profile at a time. Keep the file to preserve access; losing it requires a new address and new pairings.

```sh
tunnelite serve http://localhost:3000 --identity-file ./desktop-profile.json
```

Paired mode requires a relay that supports pairing. Use HTTPS for the relay. HTTP is accepted on loopback for tests that run entirely on the computer:

```sh
tunnelite serve http://localhost:3000 --publicUrl http://localhost:5000
```

A QR code pointing to a loopback relay cannot be used from a phone: `localhost` on the phone refers to the phone itself. To preview a site from your phone using the hosted relay, run `tunnelite http://localhost:3000`.

## Development servers with host checks

The local app receives the public tunnel hostname. If it rejects the request, allow that hostname in the app's dev-server settings.

For Vite with the hosted Tunnelite relay, you can allow Tunnelite subdomains for a dev-server run without editing its config. Stop the existing dev server first, then restart it with the environment variable below, using your project's usual package manager:

```sh
__VITE_ADDITIONAL_SERVER_ALLOWED_HOSTS=.tunnelite.com npm run dev
```

In PowerShell:

```powershell
$env:__VITE_ADDITIONAL_SERVER_ALLOWED_HOSTS = ".tunnelite.com"
npm run dev
```

See [Vite's allowed-host documentation](https://vite.dev/config/server-options#server-allowedhosts) for allowing a single hostname instead.

## Local HTTPS

Ordinary tunnels keep their existing development-certificate behavior. Paired mode verifies local HTTPS certificates by default. For an untrusted loopback development certificate, explicitly pass `--insecure-local`.

See [security](security.md), [relay protocol](relay-protocol.md), and [platform support](cross-platform.md).
