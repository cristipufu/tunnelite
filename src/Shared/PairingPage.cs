namespace Tunnelite.Protocol;

/// <summary>Shared, cached documents for browser enrollment and the relay's early access rejection.</summary>
internal static class PairingPage
{
    internal const string ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'";
    private const string RequiredTitle = "This browser isn't paired";
    private const string RequiredMessage = "Authorize this browser using a fresh pairing QR code or link from your desktop.";

    internal static readonly string Loading = Render(pairing: true);
    internal static readonly string Required = Render(pairing: false);

    private static string Render(bool pairing) => BrowserPage.Render(
        title: pairing ? "Pair with your desktop" : RequiredTitle,
        message: pairing ? "Verifying pairing code and connecting to your local tunnel…" : RequiredMessage,
        badge: "Paired Access",
        state: pairing ? "loading" : "required",
        icons: Icons,
        help: Help,
        footer: pairing
            ? "<span id=\"device\">Secure browser session</span><span class=\"host\" id=\"host\"></span>"
            : "<span id=\"device\">Paired browsers only</span><span class=\"host\" id=\"host\"></span>",
        script: pairing ? "<script src=\"/_tunnelite/pair.js\"></script>" : "");

    private const string Icons = """
        <svg class="icon icon-required" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
          <rect x="4" y="10" width="16" height="11" rx="3"></rect>
          <path d="M8 10V7a4 4 0 0 1 8 0v3"></path>
          <circle cx="12" cy="15" r="1" fill="currentColor" stroke="none"></circle>
          <path d="M12 16v2"></path>
        </svg>
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
        """;

    private const string Help = """
        <div class="steps" id="help">
          <div class="steps-title">How to connect this browser</div>
          <div class="step"><span class="step-num">1</span><span>Open the desktop terminal running <strong>tunnelite</strong></span></div>
          <div class="step"><span class="step-num">2</span><span>Press <kbd>p</kbd> for a fresh pairing QR code and link</span></div>
          <div class="step"><span class="step-num">3</span><span>Scan the QR on your phone, or open the pairing link in this browser</span></div>
        </div>
        """;

    internal static readonly string Script = $$"""
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
        if (deviceEl) deviceEl.textContent = device + ' · Browser session';
        const showError = (heading, message) => {
          if (card) card.dataset.state = 'error';
          if (title) title.textContent = heading;
          if (status) status.textContent = message;
        };
        if (!code) {
          if (card) card.dataset.state = 'required';
          if (title) title.textContent = "{{RequiredTitle}}";
          if (status) status.textContent = "{{RequiredMessage}}";
          if (deviceEl) deviceEl.textContent = 'Paired browsers only';
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
}
