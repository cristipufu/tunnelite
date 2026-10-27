<p align="center">
  <a href="https://tunnelite.com">
    <img src="https://raw.githubusercontent.com/cristipufu/tunnelite/master/src/Tunnelite.Server/wwwroot/apple-touch-icon.png" width="96" height="96" alt="Tunnelite logo">
  </a>
</p>

<h1 align="center">Tunnelite</h1>

<p align="center">
  <strong>Your localhost. One link away.</strong><br>
  Share a local app, test a webhook, or connect your phone to your desktop.
</p>

<p align="center">
  <a href="https://github.com/cristipufu/tunnelite/releases/latest"><img src="https://img.shields.io/github/v/release/cristipufu/tunnelite?style=flat-square&amp;label=release&amp;labelColor=0f172a&amp;color=0ea5e9" alt="Latest release" height="22"></a>
  <a href="https://www.nuget.org/packages/Tunnelite/"><img src="https://img.shields.io/nuget/v/Tunnelite?style=flat-square&amp;logo=nuget&amp;logoColor=white&amp;label=NuGet&amp;labelColor=0f172a&amp;color=0284c7" alt="NuGet version" height="22"></a>
  <a href="https://github.com/cristipufu/tunnelite/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/cristipufu/tunnelite/ci.yml?branch=master&amp;style=flat-square&amp;label=build&amp;logo=github&amp;logoColor=white&amp;labelColor=0f172a" alt="Build status" height="22"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-64748b?style=flat-square&amp;labelColor=0f172a" alt="MIT license" height="22"></a>
</p>

<p align="center">
  <a href="#quick-start"><strong>Get started</strong></a> &nbsp;·&nbsp;
  <a href="https://tunnelite.com">Website</a> &nbsp;·&nbsp;
  <a href="docs/sdk.md">SDK docs</a> &nbsp;·&nbsp;
  <a href="https://webhooks.tunnelite.com">Webhook tester</a>
</p>

<p align="center">
  <a href="#installation"><img src="https://img.shields.io/badge/Windows-0078d4?style=flat-square" alt="Windows supported" height="22"></a>
  <a href="#installation"><img src="https://img.shields.io/badge/macOS-334155?style=flat-square&amp;logo=apple&amp;logoColor=white" alt="macOS supported" height="22"></a>
  <a href="#installation"><img src="https://img.shields.io/badge/Linux-fcc624?style=flat-square&amp;logo=linux&amp;logoColor=black" alt="Linux supported" height="22"></a>
</p>

<p align="center">
  <sub>HTTP · WebSockets · Server-Sent Events</sub>
</p>

---

## Quick start

```bash
# macOS / Linux with Homebrew
brew install tunnelite
tunnelite http://localhost:3000
```

You can also install the [.NET tool](#net-tool) or download a [standalone binary](#standalone-binaries).

## Installation

### Homebrew

On macOS and Linux, install from [Homebrew Core](https://formulae.brew.sh/formula/tunnelite):

```bash
brew install tunnelite
```

Homebrew installs the required .NET dependency automatically. No additional tap is needed.

To update an existing installation:

```bash
brew update
brew upgrade tunnelite
```

### .NET tool

Requires the .NET 10 SDK. To install Tunnelite as a global tool, use the following command:

```bash
dotnet tool install --global Tunnelite
```

To update an existing installation:

```bash
dotnet tool update --global Tunnelite
```

### Standalone binaries

Every [release](https://github.com/cristipufu/tunnelite/releases/latest) also ships a single self-contained executable, built with NativeAOT, that needs no .NET runtime installed:

| Platform | Archive |
| --- | --- |
| Linux x64 | `tunnelite-linux-x64.tar.gz` |
| Linux arm64 | `tunnelite-linux-arm64.tar.gz` |
| macOS Intel | `tunnelite-osx-x64.tar.gz` |
| macOS Apple Silicon | `tunnelite-osx-arm64.tar.gz` |
| Windows x64 | `tunnelite-win-x64.zip` |

A `SHA256SUMS` file is attached to each release for verification.

## Usage

Once installed, you can use the `tunnelite` command to create a tunnel to your local application:

```bash
tunnelite http://localhost:3000
```

This command returns a public URL with an auto-generated subdomain and automatically shows a QR code in your terminal to open it on your phone.

![The tunnelite CLI connecting a local application to a public URL](https://github.com/cristipufu/tunnelite/blob/master/docs/tunnelite-cli.gif?raw=true)

| Argument / option | Description |
| --- | --- |
| `<localUrl>` | The local URL to tunnel to. Use `http://` or `https://` for web applications, or `tcp://host:port` for raw TCP (self-hosted servers only). |
| `--publicUrl <url>` | The tunnel server to connect to. Defaults to `https://tunnelite.com`. |
| `--log <level>` | Client log verbosity: `Trace`, `Debug`, `Information`, `Warning`, `Error` or `Critical`. |
| `--name <name>` | Optional label for a saved paired-tunnel setup on your computer (default: `default`). Does not choose the public hostname. |
| `--identity-file <path>` | Use an explicit desktop profile file in paired mode. |
| `--insecure-local` | Explicitly allow an untrusted HTTPS development certificate on a loopback target. Relay certificates are always verified. |

Interactive terminals show a live dashboard: requests update one activity entry instead of scrolling past. Press `c` to reset activity or `q` to quit. See [CLI controls](docs/cli.md) for device management and plain-text output.

### Test your local website from your phone

```bash
tunnelite http://localhost:3000
```

Start your website on your computer, then run the command above, replacing `3000` with your website's port. Scan the QR code with your phone's camera and open the link. Your website opens in the phone's browser. Keep your website and Tunnelite running on your computer.

Your phone doesn't need Tunnelite installed or the same Wi-Fi connection. **Anyone with the public URL can open the site.**

If your dev server rejects the tunnel's hostname, see [dev-server host settings](docs/paired-access.md#development-servers-with-host-checks).

### Only allow phones or browsers you pair

If you want access restricted to paired browsers, run this instead:

```bash
tunnelite serve http://localhost:3000
```

Scan the QR code to authorize your phone's browser and open the website. Other browsers cannot access it without pairing.

While Tunnelite is running:

- Press `p` to create a fresh QR code for another browser.
- Press `d` to see paired devices.
- Press `r` to remove a device's access.

### Optional: save separate setups

Tunnelite automatically saves the address and paired devices on your computer. To keep separate setups for different projects, give each one a name:

```bash
tunnelite serve http://localhost:3000 --name my-website
```

`my-website` is just a local profile label. It does **not** name your website or choose its public URL. Use that same name next time to reuse its address and paired devices.

See [paired access](docs/paired-access.md) for profile options and device management.

### Orchestrate your local coding agents from your phone (`tunnelite acp`)

Bridge your local **Claude Code**, **OpenAI Codex**, **Grok**, and **Google Antigravity** sessions to a paired mobile browser using [Acplite](https://github.com/cristipufu/acplite):

```bash
# Optional one-time setup: wrap terminal agent tabs so mobile prompts and tool approvals run inside your open terminal
tunnelite acp --install-hooks

# Connect all installed local coding agents (claude, codex, grok, agy)
tunnelite acp

# Or scope to a specific coding agent
tunnelite acp claude
tunnelite acp codex
tunnelite acp grok
tunnelite acp agy
```

Scan the pairing QR code on your phone to live-tail active desktop sessions, take over a session, steer active turns in real time, queue follow-up prompts for the next turn, interrupt turns, and approve or deny tool calls. With `--install-hooks`, prompts and tool approvals from your phone are injected directly into your open terminal tabs (`--hooks-status` / `--uninstall-hooks`), falling back to background session resume when hooks are not installed.

## Self-hosting

Tunnelite allows you to self-host the server, giving you full control over your tunneling infrastructure. To set up your own Tunnelite server, you'll need:

- Wildcard SSL certificate for your domain
- Wildcard DNS record pointing to your server's IP address

These allow Tunnelite to create secure subdomains for your tunnels and properly route traffic to your self-hosted server. The server project is `src/Tunnelite.Server`; the [deploy workflow](.github/workflows/deploy.yml) shows how the managed instance is published to Azure App Service.

Once your server is up, point the client at it:

```bash
tunnelite http://localhost:3000 --publicUrl https://your-server-domain
```

## .NET integration

| Package | Description |
| --- | --- |
| [Tunnelite](https://www.nuget.org/packages/Tunnelite/) | The `tunnelite` command-line tool. |
| [Tunnelite.Sdk](https://www.nuget.org/packages/Tunnelite.Sdk/) | `HttpTunnelClient` and `TcpTunnelClient` for opening tunnels from your own .NET code. |
| [Tunnelite.AspNetCore](https://www.nuget.org/packages/Tunnelite.AspNetCore/) | `app.UseTunnelite()` middleware that tunnels an ASP.NET Core app as soon as it starts. |

```csharp
var app = builder.Build();

app.UseTunnelite(); // prints the public URL once the tunnel is connected

app.Run();
```

See [SDK integration](docs/sdk.md) to embed paired access in a desktop or ASP.NET Core application.

## Development

Build and run the integration suite:

```bash
dotnet build -c Release
dotnet test test/Tunnelite.Tests -c Release --no-build
```

See [cross-platform validation](docs/cross-platform.md) for native CLI checks and [how it works](docs/how-it-works.md) for the transport diagrams.

## License

This project is licensed under the [MIT License](LICENSE).
