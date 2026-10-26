namespace Tunnelite.Protocol;

/// <summary>Common branding and layout for self-contained system pages.</summary>
/// <remarks>Arguments must be trusted application content, never request data. Callers cache the rendered documents.</remarks>
internal static class BrowserPage
{
    internal static string Render(string title, string message, string badge, string state,
        string icons, string help, string footer, string script = "") => $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
          <meta name="theme-color" content="#0b1120">
          <meta name="robots" content="noindex">
          <title>tunnelite · {{title}}</title>
          <link rel="icon" type="image/svg+xml" href="{{Favicon}}">
          <style>{{Styles}}</style>
        </head>
        <body>
          <main id="card" data-state="{{state}}">
            <div class="brand">
              <div class="brand-left">
                {{Logo}}
                <span class="wordmark">tunnelite</span>
              </div>
              <span class="badge">{{badge}}</span>
            </div>
            <div class="hero">
              <div class="icon-wrap" aria-hidden="true">
                <div class="ring"></div>
                <div class="icon-circle">{{icons}}</div>
              </div>
              <h1 id="title">{{title}}</h1>
              <p id="status" role="status">{{message}}</p>
            </div>
            {{help}}
            <div class="footer">{{footer}}</div>
          </main>
          {{script}}
        </body>
        </html>
        """;

    // Match the T mark used by the landing page; inline assets also work on tunnel subdomains.
    private const string Logo = """
        <svg class="logo" viewBox="0 0 64 64" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
          <defs><linearGradient id="tunnelite-logo" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#3b82f6"/><stop offset="1" stop-color="#22d3ee"/></linearGradient></defs>
          <rect width="64" height="64" rx="14" fill="url(#tunnelite-logo)"/>
          <path d="M20 17H50L47.3 26H38.6L32 48H22L28.6 26H17.3Z" fill="#fff" stroke="#fff" stroke-width="1.5" stroke-linejoin="round"/>
        </svg>
        """;

    private static readonly string Favicon = "data:image/svg+xml," + Uri.EscapeDataString(Logo);

    private const string Styles = """
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
          min-height: 100vh;
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
          flex-wrap: wrap;
          gap: 12px;
          padding-bottom: 20px;
          border-bottom: 1px solid var(--border);
          margin-bottom: 28px;
        }
        .brand-left { display: flex; align-items: center; gap: 10px; min-width: 0; }
        .logo { width: 32px; height: 32px; flex-shrink: 0; filter: drop-shadow(0 4px 10px rgba(56, 189, 248, 0.18)); }
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
        .icon-static { display: block; }
        h1 { font-size: 20px; font-weight: 700; letter-spacing: -0.02em; margin-bottom: 8px; }
        #status { font-size: 14px; line-height: 1.6; overflow-wrap: anywhere; color: var(--muted); }
        .steps {
          margin-top: 20px;
          text-align: left;
          background: rgba(2, 6, 23, 0.55);
          border: 1px solid var(--border);
          border-radius: 14px;
          padding: 14px 16px;
          font-size: 13px;
          color: var(--muted);
        }
        main[data-state="loading"] .steps, main[data-state="success"] .steps { display: none; }
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
          flex-wrap: wrap;
          gap: 8px;
          font-size: 12px;
          color: var(--muted);
        }
        .host { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 11px; opacity: 0.85; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 65%; }
        @media (prefers-reduced-motion: reduce) { .ring { animation: none !important; } }
        @keyframes spin { to { transform: rotate(360deg); } }
        .actions { display: flex; gap: 10px; flex-wrap: wrap; margin-top: 20px; }
        .button {
          display: inline-flex;
          align-items: center;
          justify-content: center;
          min-height: 44px;
          width: 100%;
          padding: 11px 16px;
          border: 1px solid rgba(56, 189, 248, 0.32);
          border-radius: 10px;
          background: rgba(56, 189, 248, 0.12);
          color: var(--accent);
          text-decoration: none;
          font-size: 14px;
          font-weight: 600;
        }
        .button:hover { background: rgba(56, 189, 248, 0.2); }
        a:focus-visible { outline: 2px solid var(--accent); outline-offset: 4px; }
        .footer a { color: var(--muted); text-underline-offset: 3px; }
        .footer a:hover { color: var(--text); }
        """;
}
