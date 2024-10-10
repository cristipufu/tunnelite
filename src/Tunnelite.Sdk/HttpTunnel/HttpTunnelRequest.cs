﻿#nullable disable
namespace Tunnelite.Sdk;

public class HttpTunnelRequest
{
    public string Subdomain { get; set; }
    public Guid? ClientId { get; set; }
    public string LocalUrl { get; set; }
    public string PublicUrl { get; set; }
}
