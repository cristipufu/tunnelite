using Tunnelite.Server;
using Tunnelite.Server.HttpTunnel;
using Tunnelite.Server.TcpTunnel;
using Tunnelite.Server.WsTunnel;

var builder = WebApplication.CreateBuilder(args);

builder.AddHttpTunneling();

builder.AddTcpTunneling();

builder.ConfigureSignalR();

var app = builder.Build();

app.UseStaticFiles();

app.UseFavicon();

app.UseHttpsRedirection();

app.UseWsTunneling();

app.UseHttpTunneling();

app.UseTcpTunneling();

app.Run();