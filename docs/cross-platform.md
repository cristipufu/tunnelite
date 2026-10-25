# Platform support and validation

| Platform | Native CLI targets |
| --- | --- |
| Windows | `win-x64` |
| macOS | `osx-x64`, `osx-arm64` |
| Linux | `linux-x64`, `linux-arm64` |

The SDK and ASP.NET integration target .NET 8 and .NET 10. The CLI and server target .NET 10; standalone CLI releases use NativeAOT and need no installed runtime.

## Terminals

Windows Terminal, PowerShell, cmd, and macOS/Linux terminals use the same CLI commands. QR rendering detects Unicode and ANSI support. Older consoles use an ASCII fallback with console colors; narrow terminals retain the copyable URL and explain the required width. Redirected output emits the full URL without terminal wrapping or a QR graphic.

Rendering failures leave the copyable URL available. Native exception reporting uses plain text rather than assembly-file-dependent rich exception formatting. Redirected stdin uses a background reader because the console's synchronized reader can block inside `ReadLineAsync` on Windows and Unix.

Profiles use the user's application-data directory through .NET platform APIs, or an explicit `--identity-file`. Unix permissions are guarded by platform checks. Windows process tests use the `.exe` executable and all process arguments are passed separately.

## CI

The complete integration suite runs on Windows, macOS, and Linux. Each native release architecture builds and runs the real CLI tests, including pairing, desktop profile restoration after a process restart, revocation, and clean exit.

Restore audits transitive dependencies and treats advisory warnings as failures. Integration tests cover streaming, concurrent startup and WebSockets, stale disconnects, certificate policy, save failures, single-use challenges, expiry, and revocation of active streams.

Run managed checks locally:

```sh
dotnet build -c Release
dotnet test test/Tunnelite.Tests -c Release --no-build
```

Set `TUNNELITE_CLI` to the absolute path of a built CLI to include its process-level tests. The executable is `Tunnelite.Client.exe` on Windows and `Tunnelite.Client` elsewhere.

Build a native binary on a matching machine, substituting its RID:

```sh
dotnet publish src/Tunnelite.Client -c Release -r osx-arm64 -p:PublishAot=true -p:InvariantGlobalization=true -o aot
```
