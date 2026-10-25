# Paired-access security

The relay terminates public HTTPS and can read application traffic and the desktop control credential. Trust the relay operator with that content. Browser pairing adds access control; it does not provide encryption that hides traffic from the relay.

## Credentials

Desktop identities use 32 random bytes. Their secrets authenticate protected registrations and control-plane transfers over HTTPS. Only loopback development relays can use HTTP through the protected SDK and CLI.

QR links contain a separate, short-lived pairing code in a URL fragment. The browser removes the fragment before posting the code as JSON. A challenge expires after five minutes and can be redeemed once, including under concurrent requests. Desktop state is saved before a cookie is issued.

Browser tickets are signed with HMAC-SHA256 and checked by the relay before forwarding or upgrading WebSockets. The desktop also checks the enrolled device and its token hash on every new request. Tickets are host-only, HttpOnly, SameSite=Strict, and Secure with the `__Host-` prefix over HTTPS. Cross-origin requests carrying an Origin header are refused.

Revocation removes the desktop's device record and cancels active HTTP/SSE/WebSocket streams. Expiry also cancels active streams. A revoked browser can retain a cryptographically valid ticket until expiry; the desktop refuses it because its enrollment is gone.

## Desktop storage

CLI profiles are written atomically. Unix files are created with owner-only read/write permissions; Windows files inherit the desktop user's application-data access controls. A separate file lease prevents concurrent CLI writers. No profile or session database is written on the relay.

Invalid saved device records are rejected at startup. A failed save does not acknowledge enrollment or revocation. Diagnostic callbacks are isolated from transport processing, and production hub errors omit internal exception detail.

## Dependencies and TLS compatibility

SignalR clients use patched 8.0.32/10.0.13 packages. The server uses the patched SignalR MessagePack protocol and MessagePack 2.5.303 while retaining legacy wire compatibility. Keep the deployed .NET runtime patched as well.

Ordinary HTTP tunnels retain their existing acceptance of self-signed development certificates. The new paired mode validates local certificates by default and offers an explicit loopback-only development opt-in. Relay certificates are always validated.

Audit dependencies with:

```sh
dotnet list package --vulnerable --include-transitive
```
