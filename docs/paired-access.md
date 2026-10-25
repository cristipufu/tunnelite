# Paired access

Start a paired tunnel to a web UI/API already running on the desktop:

```sh
tunnelite serve http://127.0.0.1:2584 --name desktop
```

Scan the QR with the phone's camera. The browser enrolls once and opens the app. No phone-side VPN or Tunnelite client is required. Each code enrolls one browser and expires after five minutes; browser sessions expire after 30 days.

## Devices

While the CLI runs:

- `p`: replace the pairing code and show a new QR.
- `d`: list paired devices and their IDs.
- `r`: select a device to revoke. Its active streams close too.
- `q`: close the tunnel and exit.

Redirected stdin accepts one command per line: `pair`, `devices`, `revoke <device-id>`, and `q`.

## Desktop profiles

`--name` selects a local profile, not a public hostname. Use a different profile for each app you expose concurrently. Reusing a profile keeps its address and paired devices even if the local port changes.

Profiles live in the desktop user's application-data directory under `tunnelite`. Use `--identity-file <path>` for an explicit location. A profile contains the desktop secret and device token hashes. One CLI process can open a profile at a time. Keep the file to preserve access; losing it requires a new address and new pairings.

```sh
tunnelite serve http://localhost:3000 --identity-file ./desktop-profile.json
```

Use HTTPS for the relay. HTTP is accepted on loopback for local testing:

```sh
tunnelite serve http://localhost:3000 --publicUrl http://localhost:5000
```

Ordinary tunnels keep their existing development-certificate behavior. Paired mode verifies local HTTPS certificates by default. For an untrusted loopback development certificate, explicitly pass `--insecure-local`.

See [security](security.md), [relay protocol](relay-protocol.md), and [platform support](cross-platform.md).
