# Relay identity and lifecycle

The relay uses the existing single-node SignalR deployment. It needs no disk, database, or external storage. Connection maps, routes, request queues, and browser-verification keys exist only in memory. Desktop identity and enrolled devices are restored by the desktop.

## Protected identities

A desktop generates a 32-byte base64url secret. SHA-256 derives its protected `p-<id>` hostname and a version-8 client GUID. Protected names and client IDs require the corresponding secret, supplied as a bearer credential on registration, the SignalR connection, and HTTP request-transfer endpoints.

The protected namespace remains verifiable after a relay restart. Friendly CLI profile names stay local; arbitrary friendly public names would require durable server ownership storage.

The secret is never put in a browser URL. Browser sessions use a separate signing key derived with HMAC-SHA256. Only the active connection's verification key is held by the relay.

## Registration

Connect to `/wsshttptunnel` before posting `/tunnelite/tunnel`. A registration without an active connection returns HTTP 425 and retains no route. The SDK retries while the hub's startup callback finishes.

Each connection owns one route. A private asynchronous gate coordinates that connection's registration and teardown. Routing and request processing use concurrent dictionaries and do not take the registration gate.

Registration acknowledges protected-endpoint and pairing capabilities. Protected clients refuse an older relay without those capabilities. Existing anonymous clients retain their wire format; protocol version 2 sends SSE through the streaming HTTP path to preserve headers, authentication, and status codes.

## Teardown and reconnects

Routes and pending requests belong to a specific connection instance. Disconnecting releases only that instance's route and streams. A late disconnect cannot remove a replacement registration or abort its pending requests.

The desktop reconnects and re-registers its derived address and browser-verification key. Its saved device records continue to authorize enrolled browsers. The relay reconstructs everything it needs from the active desktop connection.
