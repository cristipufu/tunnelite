# CLI dashboard and controls

Run the same commands as before:

```sh
tunnelite http://localhost:3000
tunnelite serve http://localhost:3000 --name my-website
```

## Live terminal view

ANSI terminals show an inset dashboard with a bounded content width and a connection card. HTTP(S) tunnels automatically show a QR code for the public URL; paired mode shows a pairing QR instead. TCP tunnels do not show a QR. The public URL is emphasized, URLs support terminal hyperlinks, and keyboard controls use visible keycaps. Wide windows keep comfortable outer gutters instead of stretching content to the edges. Smaller windows use a compact layout.

The dashboard resets the alternate screen's cursor origin before drawing and restores the shell screen and cursor on exit.

Requests update the latest-request row and request count in place. The request count measures requests received, including WebSocket connections and SSE requests. Proxy errors count failures forwarding traffic; an HTTP error returned by your application is still a normal forwarded response.

The dashboard keeps up to three recent error messages. Successful asset requests do not remove them. Query strings are omitted from the activity path, and terminal control characters are removed from dynamic labels and errors.

Refreshes are capped at five per second; idle updates happen once per second. Transport callbacks update counters and bounded in-memory state without writing terminal output.

## Keyboard controls

| Key | Action |
| --- | --- |
| `q` | Close the tunnel and quit. Ctrl+C also stops the CLI. |
| `c` | Reset request/proxy-error totals, the latest request, and recent errors. |
| `p` | In paired mode, replace the pairing code and display a new QR. |
| `d` | In paired mode, open or close the device list. |
| `r` | Open device selection for revocation. |
| Up/Down | Move through the device list. |
| Enter | Revoke the selected device in the revocation panel. |
| Esc | Close the device panel or cancel selection. |

The selected device stays attached to its identity if enrollment changes the list. If that device disappears, selection is cleared so Enter cannot revoke another device by accident.

## QR codes and terminal size

Wide terminals place the QR beside the connection details. Taller narrow terminals place it below. The CLI hides the entire code when it cannot fit; it never crops a QR. Resize the terminal or use the displayed URL. Paired mode includes the complete pairing link as well as the public URL.

Pairing codes expire after five minutes. An expired code is hidden; press `p` for a fresh one. QR graphics use Unicode half blocks when supported and ASCII otherwise.

## Plain text and diagnostics

Redirecting output, using a terminal without ANSI support, or passing `--log <level>` selects plain-text output. URLs are written as complete lines without formatter wrapping. Redirected output omits the QR graphic.

Normal request activity is batched into a text summary at most once per second, plus a final summary on exit. A burst of asset requests does not produce a line per asset. Recent errors are printed separately. Explicit `--log` also enables the selected level of SDK diagnostic logging.

Redirected stdin accepts commands one per line: `pair`, `devices`, `revoke <device-id>`, `clear`, and `q`.

## Validation

The CLI process tests exercise HTTP, WebSockets, SSE, request bursts, profile restoration, and revocation against both managed and NativeAOT builds. The gated SSE fixture cannot finish its response until the test receives the first event, so buffering the entire stream cannot pass.

Rendering tests cover QR fit, expiry, full pairing links, device selection changes, and literal text handling. Concurrent activity tests cover counts and bounded errors. CI runs these on Windows, Linux, and macOS, plus native CLI process tests on all release architectures.
