using WebSocketTunnel.Server;
using WebSocketTunnel.Server.HttpTunnel;
using WebSocketTunnel.Server.TcpTunnel;
using WebSocketTunnel.Server.WsTunnel;

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