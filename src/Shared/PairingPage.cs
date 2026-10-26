namespace Tunnelite.Protocol;

/// <summary>Shared, cached documents for browser enrollment and the relay's early access rejection.</summary>
internal static class PairingPage
{
    internal const string ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'";
    private const string RequiredTitle = "This browser isn't paired";
    private const string RequiredMessage = "Authorize this browser using a fresh pairing QR code or link from your desktop.";

    internal static readonly string Loading = Render(pairing: true);
    internal static readonly string Required = Render(pairing: false);

    private static string Render(bool pairing)
    {
        var state = pairing ? "loading" : "required";
        var title = pairing ? "Pair with your desktop" : RequiredTitle;
        var message = pairing ? "Verifying pairing code and connecting to your local tunnel…" : RequiredMessage;
        var session = pairing ? "Secure browser session" : "Paired browsers only";
        var script = pairing ? "<script src=\"/_tunnelite/pair.js\"></script>" : "";
        return $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
          <meta name="theme-color" content="#0b1120">
          <title>tunnelite · {{title}}</title>
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
            main[data-state="loading"] .ring { border-top-color: var(--accent); animation: spin 0.95s linear infinite; }
            main[data-state="loading"] .icon-loading { display: block; }
            main[data-state="required"] .icon-required { display: block; }
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
            main[data-state="required"] .steps, main[data-state="error"] .steps { display: block; }
            .steps-title { font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.06em; color: var(--text); margin-bottom: 10px; }
            .step { display: flex; align-items: flex-start; gap: 10px; padding: 5px 0; line-height: 1.55; }
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
            @media (prefers-reduced-motion: reduce) { .ring { animation: none !important; } }
            @keyframes spin { to { transform: rotate(360deg); } }
          </style>
        </head>
        <body>
          <main id="card" data-state="{{state}}">
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
                </div>
              </div>
              <h1 id="title">{{title}}</h1>
              <p id="status">{{message}}</p>
            </div>
            <div class="steps" id="help">
              <div class="steps-title">How to connect this browser</div>
              <div class="step"><span class="step-num">1</span><span>Open the desktop terminal running <strong>tunnelite</strong></span></div>
              <div class="step"><span class="step-num">2</span><span>Press <kbd>p</kbd> for a fresh pairing QR code and link</span></div>
              <div class="step"><span class="step-num">3</span><span>Scan the QR on your phone, or open the pairing link in this browser</span></div>
            </div>
            <div class="footer">
              <span id="device">{{session}}</span>
              <span class="host" id="host"></span>
            </div>
          </main>
          {{script}}
        </body>
        </html>
        """;
    }

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
