using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;
using Tunnelite.Sdk;
using Tunnelite.Tests.Infrastructure;

namespace Tunnelite.Tests;

public class LocalTlsTests(BareTunnelHost host) : IClassFixture<BareTunnelHost>
{
    [Theory]
    [InlineData(false, null, true)]
    [InlineData(false, false, false)]
    [InlineData(true, null, false)]
    [InlineData(true, true, true)]
    public async Task Local_tls_keeps_legacy_defaults_and_validates_paired_mode(bool paired, bool? allow, bool succeeds)
    {
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        certificateRequest.CertificateExtensions.Add(names.Build());
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        app.MapGet("/hello", () => "secure local app");
        await app.StartAsync();
        using var access = paired ? new PairedAccess(TunnelIdentity.Create()) : null;
        await using var client = new HttpTunnelClient(new HttpTunnelRequest
        {
            LocalUrl = "https://localhost:" + new Uri(app.Urls.First()).Port,
            PublicUrl = host.PublicUrl, AllowUntrustedLocalCertificates = allow, Access = access!,
        }, null);
        await client.ConnectAsync().WaitAsync(TestData.Timeout);
        using var http = new HttpClient { Timeout = TestData.Timeout };
        if (paired)
        {
            using var enrollment = await http.PostAsJsonAsync(host.PublicUrl + "/_tunnelite/pair", new { code = client.CreatePairingUrl().Split("#code=")[1] });
            enrollment.EnsureSuccessStatusCode();
        }
        using var response = await http.GetAsync(host.PublicUrl + "/hello");
        Assert.Equal(succeeds ? HttpStatusCode.OK : HttpStatusCode.BadGateway, response.StatusCode);
        if (succeeds) Assert.Equal("secure local app", await response.Content.ReadAsStringAsync());
        await client.DisposeAsync();
        await app.StopAsync();
    }

    [Fact]
    public void Certificate_bypass_cannot_be_used_for_a_remote_target() => Assert.Throws<ArgumentException>(() =>
        new HttpTunnelClient(new HttpTunnelRequest
        {
            LocalUrl = "https://example.com", PublicUrl = host.PublicUrl, AllowUntrustedLocalCertificates = true,
        }, null));
}
