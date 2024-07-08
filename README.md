# ws-tunnel-signalr

```plaintext
                               Public                                     Intranet
+--------+                +---------------+               +--------------------------------------+
|  User  |  ---http--->   | Tunnel Server |  <---wss--->  | Tunnel Client --http--> Application  |
|        |  <----------   |               |  <--------->  |               <--------              |
+--------+                +---------------+               +--------------------------------------+