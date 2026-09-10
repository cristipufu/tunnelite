# ⚡Tunnelite

[![CI](https://img.shields.io/github/actions/workflow/status/cristipufu/tunnelite/ci.yml?branch=master&label=CI&logo=github)](https://github.com/cristipufu/tunnelite/actions/workflows/ci.yml)
[![CD](https://img.shields.io/github/actions/workflow/status/cristipufu/tunnelite/deploy.yml?branch=master&label=CD&logo=microsoftazure)](https://github.com/cristipufu/tunnelite/actions/workflows/deploy.yml)
[![NuGet](https://img.shields.io/nuget/v/Tunnelite?logo=nuget&label=NuGet)](https://www.nuget.org/packages/Tunnelite/)
[![NuGet downloads](https://img.shields.io/nuget/dt/Tunnelite?logo=nuget&label=downloads)](https://www.nuget.org/packages/Tunnelite/)

Tunnelite is a .NET tool that lets you set up a secure connection between a public web address and an application running on your local machine. It effectively makes your local app accessible from the internet.

## 🚀 Use Cases

- Exposing locally-hosted web applications to the internet for testing or demo purposes.
- Quickly sharing dev builds during hackathons.
- Testing and debugging webhook integrations.
- Providing internet access to services running behind firewalls without exposing incoming ports.

## 📄 Installation

To install Tunnelite as a global tool, use the following command:

```bash
dotnet tool install --global Tunnelite
```

Once installed, you can use the `tunnelite` command to create a tunnel to your local application:
```bash
tunnelite http://localhost:3000
```

This command returns a public URL with an auto-generated subdomain. 

![alt text](https://github.com/cristipufu/tunnelite/blob/master/docs/tunnelite-cli.gif?raw=true)

## 🔍 How It Works

Tunnelite creates a bridge between your local application and the internet using a websocket connection. It streams incoming data from a public URL directly to your local server, making your local app accessible from anywhere.

The managed version of Tunnelite supports http(s) and ws(s) tunneling. If you need TCP tunneling, you'll have to host the server yourself.

### Self-Hosting Requirements
To set up your own Tunnelite server, you'll need:

- Wildcard SSL certificate for your domain
- Wildcard DNS record pointing to your server's IP address

These allow Tunnelite to create secure subdomains for your tunnels and properly route traffic to your self-hosted server.

 <br/>
<details>
  <summary>HTTP Connection</summary>
  
 <br/>

![image info](https://github.com/cristipufu/tunnelite/blob/master/docs/http_tunneling.png)

</details>

<details>
  <summary>TCP Overview</summary>
  
 <br/>

![image info](https://github.com/cristipufu/tunnelite/blob/master/docs/tcp_tunneling_global.png)

</details>

<details>
  <summary>TCP Connection</summary>
  
 <br/>

![image info](https://github.com/cristipufu/tunnelite/blob/master/docs/tcp_tunneling.png)

</details>

 <br/>

## 🧪 Tests

`test/Tunnelite.Tests` starts the tunnel server and a local app in-process and drives real traffic through the tunnel: HTTP, Server-Sent Events, WebSockets (including large and fragmented messages), TCP, and a client speaking the original WebSocket wire format. Set `TUNNELITE_CLI` to a built `tunnelite` binary to also run the CLI end-to-end.

```bash
dotnet test test/Tunnelite.Tests
```

CI runs the suite on every pull request, plus a NativeAOT publish of the CLI that is tested the same way.

## 📄 License

This project is licensed under the MIT License.
