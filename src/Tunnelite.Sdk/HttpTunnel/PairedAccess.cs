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
    internal const int MaxPairedDevices = 32;
    private const int Sha256HexLength = 64;
    private const int MaxDeviceNameLength = 64;
    private const int MaxPairingBodyBytes = 4096;
    private const int RandomTokenByteLength = 32;
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DeviceSessionLifetime = TimeSpan.FromDays(30);

    private readonly SemaphoreSlim _updates = new(1, 1);
    private readonly ConcurrentDictionary<Guid, DeviceCancellation> _revocations = new();
    private readonly ConcurrentDictionary<Guid, PairedDevice> _devices;
    private readonly Func<PairedAccessState, CancellationToken, Task>? _save;
    private readonly TimeProvider _clock;
    private readonly byte[] _key;
    private PairedAccessState _state;
    private PairingChallenge? _challenge;
    private int _disposed;

    public TunnelIdentity Identity { get; }
    public PairedAccessState State => Volatile.Read(ref _state);

    public PairedAccess(TunnelIdentity identity, PairedAccessState? state = null,
        Func<PairedAccessState, CancellationToken, Task>? save = null, TimeProvider? clock = null)
    {
        Identity = identity;
        _key = EndpointIdentity.ViewerKey(identity.Secret);
        var devices = state?.Devices ?? (state is null ? [] : throw new ArgumentException("Device records cannot be null.", nameof(state)));
        if (devices.Count > MaxPairedDevices || devices.Any(d => d is null || d.Id == Guid.Empty || d.TokenHash is not { Length: Sha256HexLength }
            || !d.TokenHash.All(char.IsAsciiHexDigit) || d.ExpiresAt <= d.CreatedAt) || devices.Select(d => d.Id).Distinct().Count() != devices.Count)
            throw new ArgumentException("The saved device records are invalid.", nameof(state));
        _devices = new(devices.Select(d => new KeyValuePair<Guid, PairedDevice>(d.Id, d with { Name = DeviceName(d.Name) })));
        _state = Snapshot(_devices.Values);
        _save = save;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Replaces the previous challenge with a single-use code that expires in five minutes.</summary>
    public PairingChallenge CreateChallenge()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var challenge = new PairingChallenge(ViewerTicket.Encode(RandomNumberGenerator.GetBytes(RandomTokenByteLength)), _clock.GetUtcNow().Add(ChallengeLifetime));
        Interlocked.Exchange(ref _challenge, challenge);
        return challenge;
    }

    public async Task RevokeAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _updates.WaitAsync(cancellationToken);
        try
        {
            var updated = Snapshot(State.Devices.Where(d => d.Id != deviceId));
            if (_save is not null) await _save(updated, cancellationToken);
            Volatile.Write(ref _state, updated);
            _devices.TryRemove(deviceId, out _);
            if (_revocations.TryRemove(deviceId, out var active))
            {
                await active.Source.CancelAsync();
                active.Source.Dispose();
            }
        }
        finally { _updates.Release(); }
    }

    internal bool Authorize(string? cookies, bool secure, out CancellationToken revoked)
    {
        revoked = default;
        if (Volatile.Read(ref _disposed) != 0) return false;
        var now = _clock.GetUtcNow();
        if (!ViewerTicket.Validate(_key, ViewerTicket.Cookie(cookies, secure), now, out var id, out var token)) return false;
        if (!_devices.TryGetValue(id, out var device) || device.ExpiresAt <= now || !CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(device.TokenHash), Encoding.ASCII.GetBytes(ViewerTicket.Hash(token)))) return false;
        var session = _revocations.GetOrAdd(id, _ => new());
        session.ExpireAfter(device.ExpiresAt - now);
        revoked = session.Token;
        if (_devices.ContainsKey(id)) return !revoked.IsCancellationRequested;
        if (_revocations.TryRemove(id, out var late)) { late.Source.Cancel(); late.Source.Dispose(); }
        return false;
    }

    internal async Task<HttpResponseMessage> PairAsync(Stream body, bool secure, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var limited = new MemoryStream();
        var buffer = new byte[MaxPairingBodyBytes + 1];
        while (limited.Length <= MaxPairingBodyBytes)
        {
            var read = await body.ReadAsync(buffer.AsMemory(0, (int)(MaxPairingBodyBytes + 1 - limited.Length)), cancellationToken);
            if (read == 0) break;
            limited.Write(buffer, 0, read);
        }
        if (limited.Length > MaxPairingBodyBytes) return Response(HttpStatusCode.RequestEntityTooLarge, "Pairing request is too large.");
        PairingRequest? request;
        try { request = JsonSerializer.Deserialize(limited.ToArray(), TunneliteJsonContext.Default.PairingRequest); }
        catch (JsonException) { return Response(HttpStatusCode.BadRequest, "Invalid pairing request."); }
        await _updates.WaitAsync(cancellationToken);
        try
        {
            var challenge = Volatile.Read(ref _challenge);
            if (challenge is null || challenge.ExpiresAt <= _clock.GetUtcNow() || request?.Code is not { Length: EndpointIdentity.SecretLength }
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(challenge.Code), Encoding.ASCII.GetBytes(request.Code))
                || Interlocked.CompareExchange(ref _challenge, null, challenge) != challenge)
                return Response(HttpStatusCode.Unauthorized, "Pairing code expired or already used. Scan a new QR code.");
            var now = _clock.GetUtcNow();
            var token = ViewerTicket.Encode(RandomNumberGenerator.GetBytes(RandomTokenByteLength));
            var device = new PairedDevice(Guid.NewGuid(), DeviceName(request.Name),
                now, now.Add(DeviceSessionLifetime), ViewerTicket.Hash(token));
            var updated = Snapshot(State.Devices.Where(d => d.ExpiresAt > now).Append(device));
            if (updated.Devices.Count > MaxPairedDevices) return Response(HttpStatusCode.Conflict, "Remove a paired device before adding another.");
            if (_save is not null) await _save(updated, cancellationToken);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _devices[device.Id] = device;
            foreach (var expired in _devices.Values.Where(d => d.ExpiresAt <= now))
            {
                _devices.TryRemove(expired.Id, out _);
                if (_revocations.TryRemove(expired.Id, out var stale)) { stale.Source.Cancel(); stale.Source.Dispose(); }
            }
            Volatile.Write(ref _state, updated);
            var response = Response(HttpStatusCode.NoContent, "");
            response.Headers.TryAddWithoutValidation("Set-Cookie", $"{ViewerTicket.CookieName(secure)}={ViewerTicket.Create(_key, device.Id, device.ExpiresAt, token)}; Path=/; HttpOnly; SameSite=Strict; Max-Age={(int)DeviceSessionLifetime.TotalSeconds}{(secure ? "; Secure" : "")}");
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
        if (!Authorize(cookies, secure, out var revoked))
        {
            var wantsHtml = request.Method == HttpMethod.Get
                && request.Headers.Accept.Any(a => string.Equals(a.MediaType, "text/html", StringComparison.OrdinalIgnoreCase));
            return (wantsHtml
                ? Response(HttpStatusCode.Unauthorized, Page, "text/html")
                : Response(HttpStatusCode.Unauthorized, "Scan a new QR code on your desktop."), default);
        }
        // The app receives only its own cookies; the desktop access credential belongs to Tunnelite.
        request.Headers.Remove("Cookie");
        var appCookies = StripViewerCookie(cookies, secure);
        if (appCookies.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", appCookies);
        return (null, revoked);
    }

    internal static string StripViewerCookie(string? cookies, bool secure)
    {
        if (string.IsNullOrEmpty(cookies)) return "";
        var prefix = ViewerTicket.CookieName(secure) + "=";
        return string.Join("; ", cookies.Split(';').Select(s => s.Trim())
            .Where(s => s.Length > 0 && !s.StartsWith(prefix, StringComparison.Ordinal)));
    }

    internal static HttpResponseMessage Response(HttpStatusCode status, string text, string type = "text/plain")
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, type) };
        response.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
        response.Headers.TryAddWithoutValidation("Referrer-Policy", "no-referrer");
        response.Headers.TryAddWithoutValidation("X-Content-Type-Options", "nosniff");
        response.Headers.TryAddWithoutValidation("Content-Security-Policy", "default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'");
        return response;
    }

    internal const string Page = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
          <meta name="theme-color" content="#0b1120">
          <title>tunnelite · Pair with your desktop</title>
          <style>
            :root {
              color-scheme: dark;
              --bg: #0b1120;
              --card: rgba(15, 23, 42, 0.88);
              --border: rgba(148, 163, 184, 0.16);
              --text: #f8fafc;
              --muted: #94a3b8;
              --accent: #38bdf8;
              --success: #22c55e;
              --error: #fb7185;
            }
            * { box-sizing: border-box; margin: 0; padding: 0; }
            body {
              min-height: 100dvh;
              display: flex;
              align-items: center;
              justify-content: center;
              padding: 20px;
              font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Inter, Roboto, sans-serif;
              background:
                radial-gradient(circle at 50% 0%, rgba(56, 189, 248, 0.16), transparent 55%),
                radial-gradient(circle at 85% 90%, rgba(14, 165, 233, 0.08), transparent 45%),
                var(--bg);
              color: var(--text);
              -webkit-font-smoothing: antialiased;
            }
            main {
              width: 100%;
              max-width: 400px;
              background: var(--card);
              border: 1px solid var(--border);
              border-radius: 20px;
              padding: 28px 24px 24px;
              box-shadow: 0 24px 48px rgba(0, 0, 0, 0.45), inset 0 1px 0 rgba(255, 255, 255, 0.06);
              backdrop-filter: blur(16px);
            }
            .brand {
              display: flex;
              align-items: center;
              justify-content: space-between;
              padding-bottom: 20px;
              border-bottom: 1px solid var(--border);
              margin-bottom: 28px;
            }
            .brand-left { display: flex; align-items: center; gap: 10px; }
            .logo {
              width: 32px;
              height: 32px;
              border-radius: 9px;
              background: rgba(56, 189, 248, 0.12);
              border: 1px solid rgba(56, 189, 248, 0.32);
              display: grid;
              place-items: center;
              color: var(--accent);
              box-shadow: 0 0 20px rgba(56, 189, 248, 0.18);
            }
            .wordmark { font-size: 16px; font-weight: 700; letter-spacing: -0.02em; }
            .badge {
              font-size: 11px;
              font-weight: 600;
              text-transform: uppercase;
              letter-spacing: 0.06em;
              padding: 4px 9px;
              border-radius: 999px;
              background: rgba(56, 189, 248, 0.1);
              color: var(--accent);
              border: 1px solid rgba(56, 189, 248, 0.22);
            }
            .hero { text-align: center; padding: 4px 4px 20px; }
            .icon-wrap {
              position: relative;
              width: 68px;
              height: 68px;
              margin: 0 auto 20px;
              display: grid;
              place-items: center;
            }
            .ring {
              position: absolute;
              inset: 0;
              border-radius: 50%;
              border: 2.5px solid rgba(56, 189, 248, 0.18);
              border-top-color: var(--accent);
              animation: spin 0.95s linear infinite;
            }
            .icon-circle {
              width: 52px;
              height: 52px;
              border-radius: 50%;
              background: rgba(56, 189, 248, 0.1);
              color: var(--accent);
              display: grid;
              place-items: center;
              transition: all 0.25s ease;
            }
            .icon { display: none; width: 24px; height: 24px; }
            main[data-state="loading"] .icon-loading { display: block; }
            main[data-state="success"] .ring { border-color: rgba(34, 197, 94, 0.35); animation: none; }
            main[data-state="success"] .icon-circle { background: rgba(34, 197, 94, 0.15); color: var(--success); }
            main[data-state="success"] .icon-success { display: block; }
            main[data-state="error"] .ring { border-color: rgba(251, 113, 133, 0.28); animation: none; }
            main[data-state="error"] .icon-circle { background: rgba(251, 113, 133, 0.14); color: var(--error); }
            main[data-state="error"] .icon-error { display: block; }
            h1 { font-size: 20px; font-weight: 700; letter-spacing: -0.02em; margin-bottom: 8px; }
            #status { font-size: 14px; line-height: 1.5; color: var(--muted); }
            .steps {
              display: none;
              margin-top: 20px;
              text-align: left;
              background: rgba(2, 6, 23, 0.55);
              border: 1px solid var(--border);
              border-radius: 14px;
              padding: 14px 16px;
              font-size: 13px;
              color: var(--muted);
            }
            main[data-state="error"] .steps { display: block; }
            .steps-title { font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.06em; color: var(--text); margin-bottom: 10px; }
            .step { display: flex; align-items: center; gap: 10px; padding: 5px 0; }
            .step-num {
              width: 20px;
              height: 20px;
              border-radius: 50%;
              background: rgba(56, 189, 248, 0.14);
              color: var(--accent);
              font-size: 11px;
              font-weight: 700;
              display: grid;
              place-items: center;
              flex-shrink: 0;
            }
            kbd {
              display: inline-block;
              padding: 1px 6px;
              font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
              font-size: 12px;
              font-weight: 600;
              color: var(--text);
              background: rgba(255, 255, 255, 0.1);
              border: 1px solid rgba(255, 255, 255, 0.18);
              border-radius: 5px;
            }
            .footer {
              margin-top: 18px;
              padding-top: 14px;
              border-top: 1px solid var(--border);
              display: flex;
              align-items: center;
              justify-content: space-between;
              font-size: 12px;
              color: var(--muted);
            }
            .host { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 11px; opacity: 0.85; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 65%; }
            @keyframes spin { to { transform: rotate(360deg); } }
          </style>
        </head>
        <body>
          <main id="card" data-state="loading">
            <div class="brand">
              <div class="brand-left">
                <div class="logo" aria-hidden="true">
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
                    <polygon points="12 2 22 12 12 22 2 12 12 2"></polygon>
                    <circle cx="12" cy="12" r="3"></circle>
                  </svg>
                </div>
                <span class="wordmark">tunnelite</span>
              </div>
              <span class="badge">Paired Access</span>
            </div>
            <div class="hero">
              <div class="icon-wrap" aria-hidden="true">
                <div class="ring"></div>
                <div class="icon-circle">
                  <svg class="icon icon-loading" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                    <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"></path>
                    <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"></path>
                  </svg>
                  <svg class="icon icon-success" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round">
                    <polyline points="20 6 9 17 4 12"></polyline>
                  </svg>
                  <svg class="icon icon-error" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
                    <circle cx="12" cy="12" r="10"></circle>
                    <line x1="12" y1="8" x2="12" y2="12"></line>
                    <line x1="12" y1="16" x2="12.01" y2="16"></line>
                  </svg>
                </div>
              </div>
              <h1 id="title">Pair with your desktop</h1>
              <p id="status">Verifying pairing code and connecting to your local tunnel…</p>
            </div>
            <div class="steps" id="help">
              <div class="steps-title">How to connect this browser</div>
              <div class="step"><span class="step-num">1</span><span>Switch to your desktop terminal running <strong>tunnelite</strong></span></div>
              <div class="step"><span class="step-num">2</span><span>Press <kbd>p</kbd> to generate a fresh pairing QR code</span></div>
              <div class="step"><span class="step-num">3</span><span>Scan the new QR code with your phone camera</span></div>
            </div>
            <div class="footer">
              <span id="device">Secure browser session</span>
              <span class="host" id="host"></span>
            </div>
          </main>
          <script src="/_tunnelite/pair.js"></script>
        </body>
        </html>
        """;
    internal const string Script = """
        const code = new URLSearchParams(location.hash.slice(1)).get('code');
        history.replaceState(null, '', location.pathname);
        const card = document.getElementById('card');
        const title = document.getElementById('title');
        const status = document.getElementById('status');
        const deviceEl = document.getElementById('device');
        const hostEl = document.getElementById('host');
        if (hostEl) hostEl.textContent = location.host;
        const ua = navigator.userAgent || '';
        const device = /iPhone/.test(ua) ? 'iPhone'
          : /iPad/.test(ua) ? 'iPad'
          : /Android/.test(ua) ? 'Android'
          : /Macintosh|Mac OS X/.test(ua) ? 'Mac'
          : /Windows/.test(ua) ? 'Windows'
          : /Linux/.test(ua) ? 'Linux'
          : 'Phone';
        if (deviceEl) deviceEl.textContent = device + ' · Encrypted cookie';
        const showError = (heading, message) => {
          if (card) card.dataset.state = 'error';
          if (title) title.textContent = heading;
          if (status) status.textContent = message;
        };
        if (!code) {
          showError('Pairing required', 'Scan a new QR code on your desktop to authorize this browser.');
        } else {
          fetch('/_tunnelite/pair', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({code, name:device})})
            .then(async r => {
              if (!r.ok) throw new Error(await r.text());
              if (card) card.dataset.state = 'success';
              if (title) title.textContent = 'Browser paired';
              if (status) status.textContent = 'Connected! Opening your local web app…';
              setTimeout(() => location.replace('/'), 280);
            })
            .catch(e => showError('Could not pair browser', e.message || 'Could not connect. Scan a new QR code.'));
        }
        """;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var cancellation in _revocations.Values) { cancellation.Source.Cancel(); cancellation.Source.Dispose(); }
    }

    private static PairedAccessState Snapshot(IEnumerable<PairedDevice> devices) => new() { Devices = Array.AsReadOnly(devices.ToArray()) };
    private static string DeviceName(string? name)
    {
        var clean = new string((name ?? "Phone").Where(c => !char.IsControl(c)).Take(MaxDeviceNameLength).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "Phone" : clean;
    }
    private sealed class DeviceCancellation
    {
        private int _expires;
        internal CancellationTokenSource Source { get; } = new();
        internal CancellationToken Token { get; }
        internal DeviceCancellation() => Token = Source.Token;
        internal void ExpireAfter(TimeSpan remaining)
        {
            if (Interlocked.Exchange(ref _expires, 1) != 0) return;
            try { Source.CancelAfter(remaining > DeviceSessionLifetime ? DeviceSessionLifetime : remaining); }
            catch (ObjectDisposedException) { } // Revocation won the race; the cached token is already cancelled.
        }
    }
}

public sealed record PairingChallenge(string Code, DateTimeOffset ExpiresAt);
public sealed record PairedDevice(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string TokenHash);
public sealed class PairedAccessState { public IReadOnlyList<PairedDevice> Devices { get; init; } = []; }
internal sealed class PairingRequest { public string? Code { get; set; } public string? Name { get; set; } }
