# AGENTS.md

Architectural invariants and engineering guidelines for coding agents working in `tunnelite`.

## 1. Deployed Relay Architecture: Single-Node, Multi-Client Server

`Tunnelite.Server` (`https://tunnelite.com`) is deployed as a **single-node relay server** (on Azure App Service behind a reverse proxy / TLS terminator) that serves **multiple independent clients and tunnels concurrently** in memory, with no disk or database state.

Every server and SDK change must preserve the following invariants:

1. **Strict Multi-Tenant Isolation**:
   - Multiple tenants (`https://<subdomain>.tunnelite.com` and concurrent TCP listeners) share the same `HttpTunnelStore`, `HttpRequestsQueue`, `WsRequestsQueue`, `SseRequestsQueue`, `TcpTunnelStore`, and `TcpClientStore` singletons in a single process.
   - **Never allow `localhost` fallback to match public tenants**: Bare `Host: localhost` routing exists solely for local/in-process testing (`TunnelHost`). On a reverse-proxied server (where `Connection.RemoteIpAddress` may be `127.0.0.1`), `HttpTunnelStore.IsLoopbackHost` must reject requests carrying forwarding headers (`X-Forwarded-For`, `X-Forwarded-Host`, `X-ARR-SSL`), and `ResolveTunnel` must only match tunnels registered with `tunnel.IsLoopback == true`. Furthermore, `localhost.<domain>` (`localhost` as a subdomain on a public domain) is a reserved subdomain and must never trigger loopback fallback.
   - **Authenticate connection identity on hubs and control endpoints**: Never trust caller-supplied `ClientId` fields in hub payloads (`TcpTunnelHub.RegisterTunnelAsync`) or allow unvalidated connections to overwrite another client's registration or listener. Always bind registrations and teardown to the authenticated connection instance (`ConnectionId` + `ClientId`).

2. **$O(1)$ / Per-Tenant Isolation on Connect, Request, and Teardown**:
   - Disconnecting one client (`OnDisconnectedAsync`) must only abort and clean up that specific connection instance's routes and pending streams (`ConnectionId`), without scanning or locking unrelated tenants' active streams (`StreamRequestsQueue` partitions by `ClientId` and prunes empty inner dictionaries).
   - Avoid `ConcurrentDictionary.Values` / `.Keys` snapshots on hot paths; they lock all buckets across all tenants and allocate a collection copy. Use the lock-free struct enumerator (`foreach (var (key, value) in dictionary)`) when scanning is required.
   - A late disconnect from a previous connection must never remove a replacement connection's tunnel registration or abort its in-flight requests (`RegistrationLifecycleTests`).

3. **Resource & Port Exhaustion Protection**:
   - Every streaming path (`HttpAppExtensions`, `HttpTunnelHub`, `TcpTunnelHub`, `StreamingContent`, `HttpTunnelClient`, `TcpTunnelClient`) must rent fixed-size buffers (`TunnelProtocol.ChunkSize`) from `ArrayPool<byte>.Shared` and return them in `finally`.
   - When a public viewer disconnects from a streaming response (such as SSE), link `deferredHttpContext.RequestAborted` to the relay transfer token and abort the tunnel's `POST` so the local app stops producing data immediately.
   - In `TcpTunnelHub`, always dispose any previous `TcpListenerContext` before starting a new one, fall back to an OS-assigned port (`0`) if a reconnecting client's preferred `PublicPort` was taken by another tenant on the same node, and dispose accepted `TcpClient` sockets if the tunnel disconnected before forwarding.

## 2. Wire Compatibility with Installed Clients

Standalone CLI binaries and NuGet SDK packages are installed on user machines and must continue working against an updated `Tunnelite.Server`:

- **Keep wire contracts compatible**: Do not rename SignalR hub methods (`NewHttpConnection`, `NewWsConnection`, `NewSseConnection`, `NewTcpConnection`, `StreamIncomingWsAsync`, `StreamIncomingWsV2Async`, `StreamOutgoingWsAsync`, `StreamOutgoingSseAsync`, `RegisterTunnelAsync`, `StreamIncomingAsync`, `StreamOutgoingAsync`), HTTP control routes (`/tunnelite/tunnel`, `/tunnelite/request/{requestId}`, `/wsshttptunnel`, `/wsstcptunnel`), or `X-T-*` / `X-TR-*` / `X-TC-*` forwarding headers without maintaining legacy support (`LegacyClientTests`).
- **Internal server refactoring is unrestricted**: Internal server types that do not cross the wire can be refactored or renamed freely.

## 3. NativeAOT & Trimming (`Tunnelite.Client` & `Tunnelite.Sdk`)

- `Tunnelite.Client` is published with `PublishAot=true` across Linux, macOS, and Windows.
- Never introduce reflection-based JSON or MessagePack serialization in `Tunnelite.Sdk` or `Tunnelite.Client`. Register JSON types on `TunneliteJsonContext` / `CliJsonContext` and MessagePack shapes on `TunneliteWitness`.

## 4. Terminal Security & Live Dashboard (`Tunnelite.Client`)

- Untrusted input (request paths, error messages, device names, user-agent strings) must pass through `TerminalText.Clean(...)` and be rendered as literal `Spectre.Console.Text` (never `Markup`), preventing ANSI escape or terminal control injection.
- Avoid redundant full-screen redraws when idle: only tick the live view revision when `Status != "Connected"` (spinner animation) or when an active pairing QR expiration countdown is visible.

## 5. Build & Verification Checklist

Before committing any change, verify zero warnings and run the full test suite (including end-to-end CLI tests):

```bash
dotnet build -c Release
TUNNELITE_CLI="$(pwd)/src/Tunnelite.Client/bin/Release/net10.0/Tunnelite.Client" dotnet test test/Tunnelite.Tests -c Release --no-build
```
