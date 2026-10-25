# How Tunnelite works

Tunnelite creates a bridge between your local application and the internet using a WebSocket connection. It streams incoming data from a public URL directly to your local server, making your local app accessible from anywhere.

The managed version of Tunnelite supports HTTP(S), WebSocket (WS/WSS) and Server-Sent Events tunneling. If you need TCP tunneling, you'll have to host the server yourself.

## HTTP connection

```mermaid
sequenceDiagram
    actor EC as External Client
    participant PTS as Public Tunneling Server
    participant AS as SignalR hub
    participant TC as Tunneling Client
    participant IS as Intranet Server

    EC->>PTS: 1. HTTP Request
    PTS->>AS: 2. Notify Client (based on subdomain)
    AS-->>TC: 3. WSS Notification
    TC->>PTS: 4. HTTP GET
    PTS-->>TC: 5. Stream HTTP Request Body
    TC->>IS: 6. Forward Request
    IS-->>TC: 7. Forward Response
    TC->>PTS: 8. HTTP POST
    PTS-->>EC: 9. Stream Response
    Note right of PTS: The relay holds active requests<br/>in memory until they finish
```

<details>
<summary><b>TCP overview</b></summary>

<br/>

```mermaid
sequenceDiagram
    actor TC as Tunneling Client
    box rgb(222, 235, 247) Public Tunneling Server
        participant WS as WebSocket Handler
        participant TL as TCP Listener
    end
    actor EC as External Client

    TC->>WS: Connect via WebSocket
    TC->>WS: Register tunnel request
    WS->>TL: Start listening on random port
    TL-->>WS: Port number
    WS-->>TC: Tunnel registered (port number)

    Note over TC,EC: Some time later

    EC->>TL: Connect to generated port
    TL->>WS: New TCP connection
    WS-->>TC: Notify of new TCP connection
    TC->>WS: Begin tunneling data
    WS<<->>TC: Bi-directional data transfer
    TL<<->>EC: Bi-directional data transfer
```

</details>

<details>
<summary><b>TCP connection</b></summary>

<br/>

```mermaid
sequenceDiagram
    actor EC as External Client
    participant PTS as Public Tunneling Server
    participant AS as SignalR hub
    participant TC as Tunneling Client
    participant IS as Intranet Server

    EC->>PTS: 1. TCP Connection
    PTS->>AS: 2. Notify Client (NewTcpConnection)
    AS-->>TC: 3. WSS Notification (contains connection details)
    TC->>IS: 4. Open TCP Connection
    TC->>PTS: 5. Start StreamIncomingAsync
    PTS-->>TC: 6. Stream TCP Data (incoming)
    TC->>IS: 7. Forward Incoming Data
    IS-->>TC: 8. Send Outgoing Data
    TC->>PTS: 9. StreamOutgoingAsync (with outgoing data)
    PTS-->>EC: 10. Forward Outgoing Data
    Note right of PTS: Steps 5-6 and 9-10 use<br/>bi-directional streaming<br/>over SignalR
```

</details>

The relay keeps connection and request state in memory. Paired desktop identity and device records stay on the desktop; the relay needs no disk or database. See [relay protocol](relay-protocol.md) for registration and [security](security.md) for the trust model.
