using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tunnelite.Protocol;

namespace Tunnelite.Sdk;

/// <summary>Browser enrollment, device sessions and revocation owned by the desktop, never stored on the relay.</summary>
public sealed class PairedAccess : IDisposable
{
    private readonly SemaphoreSlim _updates = new(1, 1);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _revocations = new();
    private readonly Func<PairedAccessState, CancellationToken, Task>? _save;
    private readonly TimeProvider _clock;
    private readonly byte[] _key;
    private PairedAccessState _state;
    private PairingChallenge? _challenge;

    public TunnelIdentity Identity { get; }
    public PairedAccessState State => Volatile.Read(ref _state);

    public PairedAccess(TunnelIdentity identity, PairedAccessState? state = null,
        Func<PairedAccessState, CancellationToken, Task>? save = null, TimeProvider? clock = null)
    {
        Identity = identity;
        _key = EndpointIdentity.ViewerKey(identity.Secret);
        _state = state ?? new();
        _save = save;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Replaces the previous challenge with a single-use code that expires in five minutes.</summary>
    public PairingChallenge CreateChallenge()
    {
        var challenge = new PairingChallenge(ViewerTicket.Encode(RandomNumberGenerator.GetBytes(32)), _clock.GetUtcNow().AddMinutes(5));
        Interlocked.Exchange(ref _challenge, challenge);
        return challenge;
    }

    public async Task RevokeAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await _updates.WaitAsync(cancellationToken);
        try
        {
            var updated = new PairedAccessState { Devices = State.Devices.Where(d => d.Id != deviceId).ToArray() };
            if (_save is not null) await _save(updated, cancellationToken);
            Volatile.Write(ref _state, updated);
            if (_revocations.TryGetValue(deviceId, out var active)) await active.CancelAsync();
        }
        finally { _updates.Release(); }
    }

    internal bool Authorize(string? cookies, bool secure, out CancellationToken revoked)
    {
        revoked = default;
        var now = _clock.GetUtcNow();
        if (!ViewerTicket.Validate(_key, ViewerTicket.Cookie(cookies, secure), now, out var id, out var token)) return false;
        var device = State.Devices.FirstOrDefault(d => d.Id == id && d.ExpiresAt > now);
        if (device is null || !CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(device.TokenHash), Encoding.ASCII.GetBytes(ViewerTicket.Hash(token)))) return false;
        revoked = _revocations.GetOrAdd(id, _ => new()).Token;
        return !revoked.IsCancellationRequested && State.Devices.Any(d => d.Id == id);
    }

    internal async Task<HttpResponseMessage> PairAsync(Stream body, bool secure, CancellationToken cancellationToken)
    {
        using var limited = new MemoryStream();
        var buffer = new byte[4097];
        while (limited.Length <= 4096)
        {
            var read = await body.ReadAsync(buffer.AsMemory(0, (int)(4097 - limited.Length)), cancellationToken);
            if (read == 0) break;
            limited.Write(buffer, 0, read);
        }
        if (limited.Length > 4096) return Response(HttpStatusCode.RequestEntityTooLarge, "Pairing request is too large.");
        PairingRequest? request;
        try { request = JsonSerializer.Deserialize(limited.ToArray(), TunneliteJsonContext.Default.PairingRequest); }
        catch (JsonException) { return Response(HttpStatusCode.BadRequest, "Invalid pairing request."); }
        await _updates.WaitAsync(cancellationToken);
        try
        {
            var challenge = Volatile.Read(ref _challenge);
            if (challenge is null || challenge.ExpiresAt <= _clock.GetUtcNow() || request?.Code is not { Length: 43 }
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(challenge.Code), Encoding.ASCII.GetBytes(request.Code))
                || Interlocked.CompareExchange(ref _challenge, null, challenge) != challenge)
                return Response(HttpStatusCode.Unauthorized, "Pairing code expired or already used. Scan a new QR code.");
            var now = _clock.GetUtcNow();
            var token = ViewerTicket.Encode(RandomNumberGenerator.GetBytes(32));
            var device = new PairedDevice(Guid.NewGuid(), string.IsNullOrWhiteSpace(request.Name) ? "Phone" : request.Name[..Math.Min(64, request.Name.Length)],
                now, now.AddDays(30), ViewerTicket.Hash(token));
            var updated = new PairedAccessState { Devices = State.Devices.Where(d => d.ExpiresAt > now).Append(device).ToArray() };
            if (updated.Devices.Count > 32) return Response(HttpStatusCode.Conflict, "Remove a paired device before adding another.");
            if (_save is not null) await _save(updated, cancellationToken);
            Volatile.Write(ref _state, updated);
            var response = Response(HttpStatusCode.NoContent, "");
            response.Headers.TryAddWithoutValidation("Set-Cookie", $"{ViewerTicket.CookieName(secure)}={ViewerTicket.Create(_key, device.Id, device.ExpiresAt, token)}; Path=/; HttpOnly; SameSite=Strict; Max-Age=2592000{(secure ? "; Secure" : "")}");
            return response;
        }
        finally { _updates.Release(); }
    }

    internal async Task<(HttpResponseMessage? Response, CancellationToken Revoked)> CheckHttpAsync(
        HttpRequestMessage request, string path, Uri origin, CancellationToken cancellationToken)
    {
        var secure = origin.Scheme == "https";
        if (request.Headers.TryGetValues("Origin", out var origins)
            && origins.Any(value => !string.Equals(value, origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)))
            return (Response(HttpStatusCode.Forbidden, "Origin refused."), default);
        if (path == "/_tunnelite/pair.js" && request.Method == HttpMethod.Get)
            return (Response(HttpStatusCode.OK, Script, "text/javascript"), default);
        if (path == "/_tunnelite/pair")
        {
            if (request.Method == HttpMethod.Get) return (Response(HttpStatusCode.OK, Page, "text/html"), default);
            if (request.Method != HttpMethod.Post) return (Response(HttpStatusCode.MethodNotAllowed, "Method refused."), default);
            if (request.Content?.Headers.ContentType?.MediaType != "application/json")
                return (Response(HttpStatusCode.UnsupportedMediaType, "A JSON pairing request is required."), default);
            return (await PairAsync(await request.Content.ReadAsStreamAsync(cancellationToken), secure, cancellationToken), default);
        }
        var cookies = request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : null;
        if (!Authorize(cookies, secure, out var revoked)) return (Response(HttpStatusCode.Unauthorized, "Scan a new QR code on your desktop."), default);
        // The app receives only its own cookies; the desktop access credential belongs to Tunnelite.
        request.Headers.Remove("Cookie");
        var remaining = cookies!.Split(';').Select(s => s.Trim())
            .Where(s => !s.StartsWith(ViewerTicket.CookieName(secure) + "=", StringComparison.Ordinal));
        var appCookies = string.Join("; ", remaining);
        if (appCookies.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", appCookies);
        return (null, revoked);
    }

    internal static HttpResponseMessage Response(HttpStatusCode status, string text, string type = "text/plain")
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, type) };
        response.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
        response.Headers.TryAddWithoutValidation("Referrer-Policy", "no-referrer");
        response.Headers.TryAddWithoutValidation("X-Content-Type-Options", "nosniff");
        response.Headers.TryAddWithoutValidation("Content-Security-Policy", "default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'");
        return response;
    }

    internal const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Pair with your desktop</title></head>
        <body><main><h1>Pair with your desktop</h1><p id="status">Connecting…</p></main><script src="/_tunnelite/pair.js"></script></body></html>
        """;
    internal const string Script = """
        const code = new URLSearchParams(location.hash.slice(1)).get('code');
        history.replaceState(null, '', location.pathname);
        const status = document.getElementById('status');
        if (!code) status.textContent = 'Scan a new QR code on your desktop.';
        else fetch('/_tunnelite/pair', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({code, name:'Phone'})})
          .then(async r => { if (!r.ok) throw new Error(await r.text()); location.replace('/'); })
          .catch(e => status.textContent = e.message || 'Could not connect. Scan a new QR code.');
        """;

    public void Dispose()
    {
        foreach (var cancellation in _revocations.Values) { cancellation.Cancel(); cancellation.Dispose(); }
        _updates.Dispose();
    }
}

public sealed record PairingChallenge(string Code, DateTimeOffset ExpiresAt);
public sealed record PairedDevice(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string TokenHash);
public sealed class PairedAccessState { public IReadOnlyList<PairedDevice> Devices { get; init; } = []; }
internal sealed class PairingRequest { public string? Code { get; set; } public string? Name { get; set; } }
