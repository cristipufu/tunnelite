namespace Tunnelite.Acp;

internal static class AcpMobilePage
{
    private const string LogoSvg = """
        <svg class="logo" viewBox="0 0 64 64" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
          <defs><linearGradient id="tunnelite-logo" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#3b82f6"/><stop offset="1" stop-color="#22d3ee"/></linearGradient></defs>
          <rect width="64" height="64" rx="14" fill="url(#tunnelite-logo)"/>
          <path d="M20 17H50L47.3 26H38.6L32 48H22L28.6 26H17.3Z" fill="#fff" stroke="#fff" stroke-width="1.5" stroke-linejoin="round"/>
        </svg>
        """;

    private static readonly string Favicon = "data:image/svg+xml," + Uri.EscapeDataString(LogoSvg);

    internal static readonly string Html = $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
  <meta name="theme-color" content="#0b1120">
  <meta name="apple-mobile-web-app-capable" content="yes">
  <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent">
  <meta name="robots" content="noindex">
  <title>tunnelite · ACP Agent Orchestrator</title>
  <link rel="icon" type="image/svg+xml" href="{{Favicon}}">
  <style>
    :root {
      color-scheme: dark;
      --bg: #0b1120;
      --card: rgba(15, 23, 42, 0.88);
      --surface: #0f172a;
      --surface-2: rgba(2, 6, 23, 0.55);
      --border: rgba(148, 163, 184, 0.16);
      --border-strong: rgba(56, 189, 248, 0.32);
      --text: #f8fafc;
      --text-soft: #cbd5e1;
      --muted: #94a3b8;
      --accent: #38bdf8;
      --success: #22c55e;
      --warning: #f59e0b;
      --error: #fb7185;
      --claude: #f59e0b;
      --codex: #22c55e;
      --grok: #c084fc;
      --agy: #38bdf8;
      --font: -apple-system, BlinkMacSystemFont, "Segoe UI", Inter, Roboto, sans-serif;
      --mono: ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, monospace;
      --safe-top: env(safe-area-inset-top, 0px);
      --safe-bottom: env(safe-area-inset-bottom, 0px);
    }
    * { box-sizing: border-box; margin: 0; padding: 0; -webkit-tap-highlight-color: transparent; }
    html, body {
      height: 100%;
      height: 100dvh;
      overflow: hidden;
      font-family: var(--font);
      background:
        radial-gradient(circle at 50% 0%, rgba(56, 189, 248, 0.16), transparent 55%),
        radial-gradient(circle at 85% 90%, rgba(14, 165, 233, 0.08), transparent 45%),
        var(--bg);
      color: var(--text);
      -webkit-font-smoothing: antialiased;
    }
    button, input, textarea, select {
      font: inherit;
      color: inherit;
    }
    button:focus-visible, input:focus-visible, textarea:focus-visible, select:focus-visible {
      outline: 2px solid var(--accent);
      outline-offset: 2px;
    }
    kbd {
      display: inline-block;
      padding: 1px 6px;
      font-family: var(--mono);
      font-size: 11px;
      font-weight: 600;
      color: var(--text);
      background: rgba(255, 255, 255, 0.1);
      border: 1px solid rgba(255, 255, 255, 0.18);
      border-radius: 5px;
    }
    .app-shell {
      display: flex;
      height: 100dvh;
      width: 100%;
      position: relative;
      overflow: hidden;
    }
    /* Brand & Badges (matches BrowserPage.cs) */
    .logo {
      width: 30px;
      height: 30px;
      flex-shrink: 0;
      filter: drop-shadow(0 4px 10px rgba(56, 189, 248, 0.18));
    }
    .wordmark {
      font-size: 16px;
      font-weight: 700;
      letter-spacing: -0.02em;
      color: var(--text);
    }
    .badge {
      font-size: 10px;
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      padding: 3px 8px;
      border-radius: 999px;
      background: rgba(56, 189, 248, 0.1);
      color: var(--accent);
      border: 1px solid rgba(56, 189, 248, 0.22);
      display: inline-flex;
      align-items: center;
      gap: 5px;
      white-space: nowrap;
    }
    /* Sidebar / Session Drawer */
    .sidebar {
      width: 320px;
      background: var(--card);
      backdrop-filter: blur(18px);
      border-right: 1px solid var(--border);
      box-shadow: inset -1px 0 0 rgba(255, 255, 255, 0.04);
      display: flex;
      flex-direction: column;
      flex-shrink: 0;
      z-index: 40;
      transition: transform 0.24s cubic-bezier(0.16, 1, 0.3, 1);
    }
    .sidebar-header {
      padding: calc(14px + var(--safe-top)) 16px 14px;
      border-bottom: 1px solid var(--border);
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
    }
    .brand-left {
      display: flex;
      align-items: center;
      gap: 10px;
      min-width: 0;
    }
    .brand-stack {
      display: flex;
      flex-direction: column;
      min-width: 0;
    }
    .brand-title-row {
      display: flex;
      align-items: center;
      gap: 7px;
    }
    .brand-sub {
      font-size: 11px;
      color: var(--muted);
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      margin-top: 2px;
    }
    .filter-scroll {
      display: flex;
      gap: 6px;
      padding: 10px 14px;
      overflow-x: auto;
      scrollbar-width: none;
      border-bottom: 1px solid var(--border);
    }
    .filter-scroll::-webkit-scrollbar { display: none; }
    .filter-pill {
      min-height: 30px;
      padding: 4px 11px;
      border-radius: 999px;
      border: 1px solid var(--border);
      background: rgba(255, 255, 255, 0.03);
      color: var(--muted);
      font-size: 12px;
      font-weight: 600;
      white-space: nowrap;
      cursor: pointer;
      display: inline-flex;
      align-items: center;
      gap: 5px;
      transition: all 0.15s ease;
    }
    .filter-pill.active {
      background: rgba(56, 189, 248, 0.12);
      color: var(--accent);
      border-color: var(--border-strong);
    }
    .search-box {
      padding: 10px 14px;
      border-bottom: 1px solid var(--border);
    }
    .search-input {
      width: 100%;
      min-height: 38px;
      padding: 8px 12px;
      border-radius: 10px;
      border: 1px solid var(--border);
      background: var(--surface-2);
      font-size: 13px;
      outline: none;
      transition: border-color 0.15s ease;
    }
    .search-input:focus { border-color: var(--border-strong); }
    .session-list {
      flex: 1;
      overflow-y: auto;
      padding: 10px;
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .session-card {
      text-align: left;
      width: 100%;
      padding: 12px 14px;
      border-radius: 14px;
      border: 1px solid transparent;
      background: rgba(2, 6, 23, 0.45);
      cursor: pointer;
      display: flex;
      flex-direction: column;
      gap: 7px;
      transition: background 0.15s ease, border-color 0.15s ease, transform 0.1s ease;
    }
    .session-card:hover {
      background: rgba(15, 23, 42, 0.9);
      border-color: var(--border);
    }
    .session-card.active {
      background: rgba(56, 189, 248, 0.11);
      border-color: var(--border-strong);
      box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.06);
    }
    .session-card-top {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
    }
    .provider-tag {
      font-size: 10px;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      padding: 2px 8px;
      border-radius: 999px;
      display: inline-flex;
      align-items: center;
      gap: 4px;
    }
    .provider-claude { background: rgba(245, 158, 11, 0.14); color: #fbbf24; border: 1px solid rgba(245, 158, 11, 0.28); }
    .provider-codex { background: rgba(34, 197, 94, 0.14); color: #4ade80; border: 1px solid rgba(34, 197, 94, 0.28); }
    .provider-grok { background: rgba(192, 132, 252, 0.14); color: #d8b4fe; border: 1px solid rgba(192, 132, 252, 0.28); }
    .provider-agy { background: rgba(56, 189, 248, 0.14); color: #38bdf8; border: 1px solid rgba(56, 189, 248, 0.28); }
    .session-title {
      font-size: 13px;
      font-weight: 600;
      color: var(--text);
      line-height: 1.4;
      display: -webkit-box;
      -webkit-line-clamp: 2;
      -webkit-box-orient: vertical;
      overflow: hidden;
    }
    .session-meta {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
      font-size: 11px;
      color: var(--muted);
    }
    .session-repo {
      font-family: var(--mono);
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      max-width: 210px;
      opacity: 0.9;
    }
    .status-dot {
      width: 7px;
      height: 7px;
      border-radius: 50%;
      display: inline-block;
      flex-shrink: 0;
    }
    .status-dot.busy {
      background: var(--success);
      box-shadow: 0 0 8px var(--success);
      animation: pulse 1.2s infinite ease-in-out;
    }
    .status-dot.perm {
      background: var(--warning);
      box-shadow: 0 0 8px var(--warning);
      animation: pulse 1s infinite ease-in-out;
    }
    /* Main Pane */
    .main-pane {
      flex: 1;
      display: flex;
      flex-direction: column;
      min-width: 0;
      height: 100dvh;
    }
    .topbar {
      padding: calc(10px + var(--safe-top)) 14px 10px;
      background: var(--card);
      backdrop-filter: blur(16px);
      border-bottom: 1px solid var(--border);
      box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.05);
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
      z-index: 20;
    }
    .topbar-left {
      display: flex;
      align-items: center;
      gap: 10px;
      min-width: 0;
      flex: 1;
    }
    .icon-btn {
      min-height: 38px;
      padding: 0 12px;
      border-radius: 10px;
      border: 1px solid var(--border);
      background: rgba(255, 255, 255, 0.04);
      color: var(--text);
      font-size: 12px;
      font-weight: 600;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      gap: 6px;
      cursor: pointer;
      flex-shrink: 0;
      transition: background 0.15s ease, border-color 0.15s ease, transform 0.1s ease;
    }
    .icon-btn:hover { background: rgba(255, 255, 255, 0.08); }
    .icon-btn:active { transform: scale(0.97); }
    .icon-btn.primary {
      background: rgba(56, 189, 248, 0.12);
      color: var(--accent);
      border-color: var(--border-strong);
    }
    .icon-btn.primary:hover { background: rgba(56, 189, 248, 0.2); }
    .btn-svg {
      width: 15px;
      height: 15px;
      flex-shrink: 0;
    }
    .mobile-menu-btn { display: none; }
    .active-header-info {
      min-width: 0;
      flex: 1;
    }
    .active-header-title {
      font-size: 14px;
      font-weight: 700;
      letter-spacing: -0.01em;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .active-header-sub {
      font-size: 11px;
      color: var(--muted);
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      font-family: var(--mono);
      margin-top: 2px;
    }
    /* Timeline & BrowserPage-style Empty Hero Card */
    .timeline-wrap {
      flex: 1;
      overflow-y: auto;
      padding: 16px 14px 20px;
      display: flex;
      flex-direction: column;
      scroll-behavior: smooth;
    }
    .timeline-inner {
      width: 100%;
      max-width: 820px;
      margin: 0 auto;
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    .hero-card {
      width: 100%;
      max-width: 420px;
      margin: 24px auto;
      background: var(--card);
      border: 1px solid var(--border);
      border-radius: 20px;
      padding: 26px 22px 22px;
      box-shadow: 0 24px 48px rgba(0, 0, 0, 0.45), inset 0 1px 0 rgba(255, 255, 255, 0.06);
      backdrop-filter: blur(16px);
      text-align: center;
    }
    .icon-wrap {
      position: relative;
      width: 64px;
      height: 64px;
      margin: 0 auto 18px;
      display: grid;
      place-items: center;
    }
    .ring {
      position: absolute;
      inset: 0;
      border-radius: 50%;
      border: 2.5px solid rgba(56, 189, 248, 0.22);
    }
    .icon-circle {
      width: 48px;
      height: 48px;
      border-radius: 50%;
      background: rgba(56, 189, 248, 0.1);
      color: var(--accent);
      display: grid;
      place-items: center;
    }
    .hero-card h2 {
      font-size: 19px;
      font-weight: 700;
      letter-spacing: -0.02em;
      margin-bottom: 8px;
    }
    .hero-card p {
      font-size: 14px;
      line-height: 1.6;
      color: var(--muted);
    }
    .steps {
      margin-top: 18px;
      text-align: left;
      background: var(--surface-2);
      border: 1px solid var(--border);
      border-radius: 14px;
      padding: 14px 16px;
      font-size: 13px;
      color: var(--muted);
    }
    .steps-title {
      font-size: 11px;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      color: var(--text);
      margin-bottom: 10px;
    }
    .step {
      display: flex;
      align-items: flex-start;
      gap: 10px;
      padding: 5px 0;
      line-height: 1.5;
    }
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
      margin-top: 1px;
    }
    /* Message Bubbles & Rich Code Blocks */
    .msg-row {
      display: flex;
      flex-direction: column;
      gap: 4px;
    }
    .msg-row.user { align-items: flex-end; }
    .msg-row.assistant { align-items: flex-start; }
    .msg-row.system { align-items: center; }
    .msg-bubble {
      max-width: 92%;
      padding: 12px 14px;
      border-radius: 16px;
      font-size: 14px;
      line-height: 1.6;
      overflow-wrap: anywhere;
      border: 1px solid var(--border);
      box-shadow: 0 8px 20px rgba(0, 0, 0, 0.22), inset 0 1px 0 rgba(255, 255, 255, 0.04);
    }
    .msg-row.user .msg-bubble {
      background: linear-gradient(135deg, rgba(37, 99, 235, 0.28), rgba(14, 165, 233, 0.18));
      border-color: var(--border-strong);
      border-bottom-right-radius: 5px;
    }
    .msg-row.assistant .msg-bubble {
      background: var(--card);
      backdrop-filter: blur(12px);
      border-bottom-left-radius: 5px;
      width: 100%;
    }
    .msg-badge {
      display: inline-flex;
      align-items: center;
      font-size: 10px;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      padding: 2px 7px;
      border-radius: 999px;
      margin-bottom: 6px;
      background: rgba(56, 189, 248, 0.14);
      color: var(--accent);
      border: 1px solid rgba(56, 189, 248, 0.26);
    }
    .thinking-box {
      margin-bottom: 10px;
      border-radius: 12px;
      border: 1px solid var(--border);
      background: var(--surface-2);
      overflow: hidden;
    }
    .thinking-summary {
      padding: 8px 12px;
      font-size: 12px;
      font-weight: 600;
      color: var(--muted);
      cursor: pointer;
      user-select: none;
    }
    .thinking-body {
      padding: 10px 12px;
      border-top: 1px solid var(--border);
      font-size: 12px;
      color: var(--muted);
      white-space: pre-wrap;
      font-family: var(--mono);
      max-height: 220px;
      overflow-y: auto;
      line-height: 1.5;
    }
    .msg-content {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .msg-text-seg {
      white-space: pre-wrap;
    }
    .inline-code {
      font-family: var(--mono);
      font-size: 12.5px;
      padding: 1px 5px;
      border-radius: 5px;
      background: rgba(2, 6, 23, 0.65);
      border: 1px solid var(--border);
      color: #e2e8f0;
    }
    .code-block {
      border-radius: 12px;
      border: 1px solid var(--border);
      background: rgba(2, 6, 23, 0.85);
      overflow: hidden;
    }
    .code-block-head {
      padding: 6px 12px;
      background: rgba(15, 23, 42, 0.75);
      border-bottom: 1px solid var(--border);
      display: flex;
      align-items: center;
      justify-content: space-between;
      font-family: var(--mono);
      font-size: 11px;
      color: var(--muted);
    }
    .code-copy-btn {
      background: rgba(255, 255, 255, 0.06);
      border: 1px solid var(--border);
      border-radius: 6px;
      padding: 2px 8px;
      font-family: var(--font);
      font-size: 11px;
      font-weight: 600;
      color: var(--text-soft);
      cursor: pointer;
    }
    .code-block pre {
      padding: 10px 12px;
      overflow-x: auto;
      font-family: var(--mono);
      font-size: 12px;
      line-height: 1.5;
      color: #e2e8f0;
    }
    /* Tool Call Card */
    .tool-card {
      width: 100%;
      border-radius: 14px;
      border: 1px solid var(--border);
      background: rgba(15, 23, 42, 0.75);
      overflow: hidden;
    }
    .tool-header {
      padding: 10px 14px;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
      cursor: pointer;
      font-size: 12px;
    }
    .tool-title {
      font-family: var(--mono);
      font-weight: 600;
      color: var(--text-soft);
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .tool-status {
      font-size: 10px;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      padding: 3px 8px;
      border-radius: 999px;
      flex-shrink: 0;
    }
    .tool-status.running { background: rgba(56, 189, 248, 0.14); color: var(--accent); border: 1px solid rgba(56, 189, 248, 0.25); }
    .tool-status.done { background: rgba(34, 197, 94, 0.14); color: var(--success); border: 1px solid rgba(34, 197, 94, 0.25); }
    .tool-details {
      display: none;
      padding: 10px 14px;
      border-top: 1px solid var(--border);
      background: rgba(2, 6, 23, 0.85);
      font-family: var(--mono);
      font-size: 11.5px;
      color: var(--muted);
      white-space: pre-wrap;
      max-height: 220px;
      overflow-y: auto;
      line-height: 1.5;
    }
    .tool-card.open .tool-details { display: block; }
    /* System / Turn Pill & Interruption Alert */
    .turn-pill {
      font-size: 11px;
      font-family: var(--mono);
      color: var(--muted);
      padding: 4px 12px;
      border-radius: 999px;
      border: 1px solid var(--border);
      background: rgba(15, 23, 42, 0.72);
      display: inline-flex;
      align-items: center;
      gap: 6px;
    }
    .alert-card {
      width: 100%;
      padding: 12px 14px;
      border-radius: 14px;
      border: 1px solid rgba(251, 113, 133, 0.32);
      background: rgba(251, 113, 133, 0.12);
      color: #fecdd3;
      font-size: 13px;
      line-height: 1.5;
    }
    /* Permission Approval Tray */
    .perm-tray {
      background: rgba(245, 158, 11, 0.1);
      border-top: 1px solid rgba(245, 158, 11, 0.32);
      padding: 12px 14px;
      backdrop-filter: blur(16px);
    }
    .perm-card {
      max-width: 820px;
      margin: 0 auto;
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .perm-title {
      font-size: 13px;
      font-weight: 700;
      color: #fcd34d;
      display: flex;
      align-items: center;
      justify-content: space-between;
    }
    .perm-desc {
      font-family: var(--mono);
      font-size: 12px;
      color: var(--text-soft);
      background: var(--surface-2);
      border: 1px solid var(--border);
      padding: 8px 11px;
      border-radius: 10px;
      max-height: 96px;
      overflow-y: auto;
      white-space: pre-wrap;
    }
    .perm-actions {
      display: flex;
      gap: 10px;
    }
    .perm-btn {
      flex: 1;
      min-height: 42px;
      border-radius: 10px;
      font-size: 13px;
      font-weight: 700;
      cursor: pointer;
      border: 1px solid transparent;
    }
    .perm-btn.allow {
      background: rgba(34, 197, 94, 0.18);
      color: var(--success);
      border-color: rgba(34, 197, 94, 0.38);
    }
    .perm-btn.deny {
      background: rgba(251, 113, 133, 0.14);
      color: var(--error);
      border-color: rgba(251, 113, 133, 0.32);
    }
    /* Next-Turn Queue Tray */
    .queue-tray {
      background: rgba(56, 189, 248, 0.07);
      border-top: 1px solid rgba(56, 189, 248, 0.22);
      padding: 10px 14px;
      backdrop-filter: blur(16px);
    }
    .queue-inner {
      max-width: 820px;
      margin: 0 auto;
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .queue-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      font-size: 11px;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      color: var(--accent);
    }
    .queue-item {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
      padding: 7px 11px;
      border-radius: 10px;
      background: var(--surface-2);
      border: 1px solid var(--border);
      font-size: 12.5px;
    }
    .queue-item-text {
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      flex: 1;
    }
    .queue-remove-btn {
      background: none;
      border: none;
      color: var(--muted);
      font-size: 16px;
      cursor: pointer;
      padding: 2px 6px;
    }
    /* Bottom Composer Dock */
    .composer-dock {
      background: var(--card);
      backdrop-filter: blur(20px);
      border-top: 1px solid var(--border);
      box-shadow: 0 -12px 32px rgba(0, 0, 0, 0.25), inset 0 1px 0 rgba(255, 255, 255, 0.05);
      padding: 10px 14px calc(12px + var(--safe-bottom));
      z-index: 20;
    }
    .composer-inner {
      max-width: 820px;
      margin: 0 auto;
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .mode-bar {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
      flex-wrap: wrap;
    }
    .mode-segmented {
      display: inline-flex;
      background: var(--surface-2);
      border: 1px solid var(--border);
      border-radius: 10px;
      padding: 3px;
      gap: 3px;
    }
    .mode-option {
      min-height: 28px;
      padding: 4px 11px;
      border-radius: 7px;
      border: none;
      background: transparent;
      color: var(--muted);
      font-size: 11px;
      font-weight: 700;
      cursor: pointer;
      transition: all 0.15s ease;
    }
    .mode-option.active {
      background: rgba(56, 189, 248, 0.16);
      color: var(--accent);
    }
    .mode-option.interrupt.active {
      background: rgba(245, 158, 11, 0.2);
      color: #fbbf24;
    }
    .stop-turn-btn {
      min-height: 30px;
      padding: 4px 12px;
      border-radius: 9px;
      border: 1px solid rgba(251, 113, 133, 0.32);
      background: rgba(251, 113, 133, 0.12);
      color: var(--error);
      font-size: 11px;
      font-weight: 700;
      cursor: pointer;
    }
    .quick-chips {
      display: flex;
      gap: 6px;
      overflow-x: auto;
      scrollbar-width: none;
    }
    .quick-chips::-webkit-scrollbar { display: none; }
    .quick-chip {
      min-height: 28px;
      padding: 4px 11px;
      border-radius: 999px;
      border: 1px solid var(--border);
      background: rgba(255, 255, 255, 0.03);
      color: var(--muted);
      font-size: 11.5px;
      font-weight: 500;
      white-space: nowrap;
      cursor: pointer;
    }
    .quick-chip:active {
      background: rgba(56, 189, 248, 0.14);
      color: var(--accent);
      border-color: var(--border-strong);
    }
    .input-row {
      display: flex;
      align-items: flex-end;
      gap: 8px;
    }
    .prompt-textarea {
      flex: 1;
      min-height: 44px;
      max-height: 132px;
      padding: 10px 14px;
      border-radius: 12px;
      border: 1px solid var(--border);
      background: var(--surface-2);
      font-size: 15px;
      line-height: 1.45;
      resize: none;
      outline: none;
      transition: border-color 0.15s ease;
    }
    .prompt-textarea:focus { border-color: var(--border-strong); }
    .send-btn {
      min-height: 44px;
      padding: 0 18px;
      border-radius: 12px;
      border: 1px solid var(--border-strong);
      background: rgba(56, 189, 248, 0.16);
      color: var(--accent);
      font-size: 13px;
      font-weight: 700;
      cursor: pointer;
      flex-shrink: 0;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      gap: 6px;
      transition: all 0.15s ease;
    }
    .send-btn:hover { background: rgba(56, 189, 248, 0.24); }
    .send-btn:active { transform: scale(0.97); }
    .send-btn.mode-queue {
      background: rgba(56, 189, 248, 0.18);
      color: var(--accent);
      border-color: var(--border-strong);
    }
    .send-btn.mode-steer {
      background: rgba(34, 197, 94, 0.16);
      color: var(--success);
      border-color: rgba(34, 197, 94, 0.36);
    }
    .send-btn.mode-interrupt {
      background: rgba(245, 158, 11, 0.18);
      color: #fbbf24;
      border-color: rgba(245, 158, 11, 0.38);
    }
    /* Modals & Bottom Sheets */
    .backdrop {
      position: fixed;
      inset: 0;
      background: rgba(2, 6, 23, 0.72);
      backdrop-filter: blur(4px);
      z-index: 35;
      display: none;
    }
    .backdrop.open { display: block; }
    .modal-sheet {
      position: fixed;
      left: 50%;
      bottom: 0;
      transform: translate(-50%, 105%);
      width: 100%;
      max-width: 500px;
      max-height: 88dvh;
      background: var(--card);
      backdrop-filter: blur(20px);
      border: 1px solid var(--border);
      box-shadow: 0 24px 48px rgba(0, 0, 0, 0.55), inset 0 1px 0 rgba(255, 255, 255, 0.08);
      border-top-left-radius: 22px;
      border-top-right-radius: 22px;
      padding: 12px 18px calc(22px + var(--safe-bottom));
      z-index: 50;
      transition: transform 0.25s cubic-bezier(0.16, 1, 0.3, 1);
      overflow-y: auto;
    }
    .modal-sheet.open { transform: translate(-50%, 0); }
    .sheet-handle {
      width: 36px;
      height: 4px;
      border-radius: 999px;
      background: rgba(148, 163, 184, 0.28);
      margin: 0 auto 14px;
    }
    .sheet-title {
      font-size: 16px;
      font-weight: 700;
      letter-spacing: -0.01em;
      margin-bottom: 16px;
      display: flex;
      align-items: center;
      justify-content: space-between;
    }
    .form-group {
      display: flex;
      flex-direction: column;
      gap: 6px;
      margin-bottom: 14px;
    }
    .form-label {
      font-size: 11px;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      color: var(--muted);
    }
    .form-input, .form-select {
      width: 100%;
      min-height: 42px;
      padding: 10px 12px;
      border-radius: 10px;
      border: 1px solid var(--border);
      background: var(--surface-2);
      font-size: 14px;
      outline: none;
    }
    .form-input:focus, .form-select:focus { border-color: var(--border-strong); }
    .quota-card {
      padding: 14px;
      border-radius: 14px;
      border: 1px solid var(--border);
      background: var(--surface-2);
      margin-bottom: 10px;
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .quota-bar {
      height: 6px;
      border-radius: 999px;
      background: rgba(255, 255, 255, 0.08);
      overflow: hidden;
    }
    .quota-fill {
      height: 100%;
      background: linear-gradient(90deg, #38bdf8, #22c55e);
      border-radius: 999px;
    }
    .toast-banner {
      position: fixed;
      top: calc(62px + var(--safe-top));
      left: 50%;
      transform: translate(-50%, -20px);
      max-width: min(92vw, 460px);
      padding: 10px 14px;
      border-radius: 12px;
      background: var(--card);
      backdrop-filter: blur(16px);
      border: 1px solid rgba(251, 113, 133, 0.42);
      color: #fecdd3;
      font-size: 12.5px;
      font-weight: 600;
      z-index: 60;
      opacity: 0;
      pointer-events: none;
      transition: opacity 0.2s ease, transform 0.2s ease;
      box-shadow: 0 16px 36px rgba(0, 0, 0, 0.5);
    }
    .toast-banner.show {
      opacity: 1;
      transform: translate(-50%, 0);
    }
    @keyframes pulse {
      0%, 100% { opacity: 1; transform: scale(1); }
      50% { opacity: 0.45; transform: scale(0.92); }
    }
    @media (max-width: 768px) {
      .sidebar {
        position: fixed;
        top: 0;
        bottom: 0;
        left: 0;
        width: min(86vw, 330px);
        transform: translateX(-104%);
      }
      .sidebar.open { transform: translateX(0); }
      .mobile-menu-btn { display: inline-flex; }
      /* Prevent iOS Safari from auto-zooming on input focus */
      input, textarea, select { font-size: 16px !important; }
    }
  </style>
</head>
<body>
  <div class="app-shell">
    <div id="drawerBackdrop" class="backdrop"></div>
    <div id="toastBanner" class="toast-banner" role="status"></div>

    <aside id="sidebar" class="sidebar">
      <div class="sidebar-header">
        <div class="brand-left">
          {{LogoSvg}}
          <div class="brand-stack">
            <div class="brand-title-row">
              <span class="wordmark">tunnelite</span>
              <span class="badge">ACP</span>
            </div>
            <div id="machineLabel" class="brand-sub">Connecting to desktop…</div>
          </div>
        </div>
        <button id="refreshSessionsBtn" class="icon-btn" title="Sync local sessions">
          <svg class="btn-svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 0 0-9-9 9.75 9.75 0 0 0-6.74 2.74L3 8"/><path d="M3 3v5h5"/><path d="M3 12a9 9 0 0 0 9 9 9.75 9.75 0 0 0 6.74-2.74L21 16"/><path d="M16 16h5v5"/></svg>
          <span>Sync</span>
        </button>
      </div>

      <div id="providerFilterBar" class="filter-scroll"></div>

      <div class="search-box">
        <input id="sessionSearchInput" class="search-input" type="search" placeholder="Filter by title, repo, or branch…">
      </div>

      <div id="sessionList" class="session-list"></div>
    </aside>

    <main class="main-pane">
      <header class="topbar">
        <div class="topbar-left">
          <button id="openDrawerBtn" class="icon-btn mobile-menu-btn" aria-label="Open sessions drawer">
            <svg class="btn-svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="3" y1="6" x2="21" y2="6"/><line x1="3" y1="12" x2="21" y2="12"/><line x1="3" y1="18" x2="15" y2="18"/></svg>
            <span>Sessions</span>
          </button>
          <div class="active-header-info">
            <div id="activeSessionTitle" class="active-header-title">Select or start a session</div>
            <div id="activeSessionMeta" class="active-header-sub">Ready to orchestrate local agents</div>
          </div>
        </div>
        <div style="display:flex;align-items:center;gap:6px;">
          <span id="connStatusPill" class="badge"><span class="status-dot busy"></span>LIVE</span>
          <button id="openQuotasBtn" class="icon-btn" title="Installed agents and usage">
            <svg class="btn-svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="4" y="4" width="16" height="16" rx="2"/><rect x="9" y="9" width="6" height="6"/><path d="M9 1v3M15 1v3M9 20v3M15 20v3M20 9h3M20 14h3M1 9h3M1 14h3"/></svg>
            <span>Agents</span>
          </button>
          <button id="openNewSessionBtn" class="icon-btn primary">
            <svg class="btn-svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>
            <span>New</span>
          </button>
        </div>
      </header>

      <div id="timelineWrap" class="timeline-wrap">
        <div id="timelineInner" class="timeline-inner"></div>
      </div>

      <div id="permTray" class="perm-tray" style="display:none;"></div>
      <div id="queueTray" class="queue-tray" style="display:none;"></div>

      <footer class="composer-dock">
        <div class="composer-inner">
          <div id="busyModeBar" class="mode-bar" style="display:none;">
            <div class="mode-segmented">
              <button type="button" class="mode-option active" data-mode="queue">Queue Next</button>
              <button type="button" class="mode-option" data-mode="steer">Steer Live</button>
              <button type="button" class="mode-option interrupt" data-mode="interrupt">Interrupt</button>
            </div>
            <button id="stopTurnBtn" type="button" class="stop-turn-btn">Stop Turn</button>
          </div>

          <div class="quick-chips">
            <button type="button" class="quick-chip" data-chip="Summarize current progress and next steps.">Summarize status</button>
            <button type="button" class="quick-chip" data-chip="Run the test suite and fix any failures.">Run tests</button>
            <button type="button" class="quick-chip" data-chip="Review git status and diff for any unintended changes.">Check git diff</button>
            <button type="button" class="quick-chip" data-chip="Continue with the implementation.">Continue</button>
          </div>

          <form id="composerForm" class="input-row">
            <textarea id="promptInput" class="prompt-textarea" rows="1" placeholder="Send prompt, steer live, or queue for next turn…"></textarea>
            <button id="sendBtn" type="submit" class="send-btn">Send</button>
          </form>
        </div>
      </footer>
    </main>

    <!-- New Session Bottom Sheet -->
    <div id="newSessionSheet" class="modal-sheet">
      <div class="sheet-handle"></div>
      <div class="sheet-title">
        <span>Start New Agent Session</span>
        <button id="closeNewSessionBtn" type="button" class="icon-btn">Close</button>
      </div>
      <form id="newSessionForm">
        <div class="form-group">
          <label class="form-label" for="newProviderSelect">Coding Agent</label>
          <select id="newProviderSelect" class="form-select"></select>
        </div>
        <div class="form-group">
          <label class="form-label" for="newCwdInput">Working Directory on Desktop</label>
          <input id="newCwdInput" class="form-input" type="text" required>
          <div id="recentDirsChips" class="quick-chips" style="margin-top:4px;"></div>
        </div>
        <div class="form-group">
          <label class="form-label" for="newModelInput">Model Override (optional)</label>
          <input id="newModelInput" class="form-input" type="text" placeholder="Default provider model">
        </div>
        <div class="form-group" style="flex-direction:row;align-items:center;justify-content:space-between;">
          <label class="form-label" for="newAutoApproveCheck" style="margin:0;">Auto-approve tool executions</label>
          <input id="newAutoApproveCheck" type="checkbox" style="width:18px;height:18px;">
        </div>
        <div class="form-group">
          <label class="form-label" for="newPromptInput">Initial Prompt</label>
          <textarea id="newPromptInput" class="prompt-textarea" rows="3" placeholder="What should the agent build or investigate?"></textarea>
        </div>
        <button type="submit" class="send-btn" style="width:100%;min-height:44px;">Launch Session</button>
      </form>
    </div>

    <!-- Providers & Quotas Bottom Sheet -->
    <div id="quotasSheet" class="modal-sheet">
      <div class="sheet-handle"></div>
      <div class="sheet-title">
        <span>Installed Agents &amp; Quotas</span>
        <div style="display:flex;gap:6px;">
          <button id="probeQuotasBtn" type="button" class="icon-btn primary">Probe Quotas</button>
          <button id="closeQuotasBtn" type="button" class="icon-btn">Close</button>
        </div>
      </div>
      <div id="quotasList"></div>
    </div>
  </div>
  <script src="/app.js"></script>
</body>
</html>
""";

    internal static readonly string Script = """
(() => {
  const state = {
    machineName: '',
    defaultWorkingDirectory: '',
    scopedProvider: null,
    providers: [],
    usageSnapshots: [],
    sessions: [],
    recentDirectories: [],
    activeSessionKey: null,
    activeSession: null,
    providerFilter: 'all',
    searchQuery: '',
    busyActionMode: 'queue',
    ws: null,
    sse: null
  };

  const el = id => document.getElementById(id);
  const sidebar = el('sidebar');
  const drawerBackdrop = el('drawerBackdrop');
  const machineLabel = el('machineLabel');
  const providerFilterBar = el('providerFilterBar');
  const sessionSearchInput = el('sessionSearchInput');
  const sessionList = el('sessionList');
  const activeSessionTitle = el('activeSessionTitle');
  const activeSessionMeta = el('activeSessionMeta');
  const connStatusPill = el('connStatusPill');
  const timelineWrap = el('timelineWrap');
  const timelineInner = el('timelineInner');
  const permTray = el('permTray');
  const queueTray = el('queueTray');
  const busyModeBar = el('busyModeBar');
  const promptInput = el('promptInput');
  const sendBtn = el('sendBtn');
  const newSessionSheet = el('newSessionSheet');
  const quotasSheet = el('quotasSheet');
  const toastBanner = el('toastBanner');
  let toastTimer = null;

  function showToast(message) {
    if (!message || !toastBanner) return;
    toastBanner.textContent = message;
    toastBanner.classList.add('show');
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(() => toastBanner.classList.remove('show'), 3800);
  }

  function resizePromptTextarea() {
    promptInput.style.height = 'auto';
    promptInput.style.height = Math.min(promptInput.scrollHeight, 132) + 'px';
  }

  promptInput.addEventListener('input', resizePromptTextarea);

  function closeOverlays() {
    sidebar.classList.remove('open');
    newSessionSheet.classList.remove('open');
    quotasSheet.classList.remove('open');
    drawerBackdrop.classList.remove('open');
  }

  el('openDrawerBtn').addEventListener('click', () => {
    sidebar.classList.add('open');
    drawerBackdrop.classList.add('open');
  });
  drawerBackdrop.addEventListener('click', closeOverlays);
  el('closeNewSessionBtn').addEventListener('click', closeOverlays);
  el('closeQuotasBtn').addEventListener('click', closeOverlays);

  el('openNewSessionBtn').addEventListener('click', () => {
    populateNewSessionModal();
    newSessionSheet.classList.add('open');
    drawerBackdrop.classList.add('open');
  });

  el('openQuotasBtn').addEventListener('click', () => {
    renderQuotasModal();
    quotasSheet.classList.add('open');
    drawerBackdrop.classList.add('open');
  });

  el('refreshSessionsBtn').addEventListener('click', () => {
    sendAction({ action: 'refresh_sessions', sessionKey: state.activeSessionKey });
  });

  el('probeQuotasBtn').addEventListener('click', () => {
    el('probeQuotasBtn').textContent = 'Probing…';
    sendAction({ action: 'probe_usage', sessionKey: state.activeSessionKey }).finally(() => {
      el('probeQuotasBtn').textContent = 'Probe Quotas';
    });
  });

  sessionSearchInput.addEventListener('input', e => {
    state.searchQuery = (e.target.value || '').trim().toLowerCase();
    renderSessionList();
  });

  document.querySelectorAll('.mode-option').forEach(btn => {
    btn.addEventListener('click', () => {
      state.busyActionMode = btn.dataset.mode;
      updateComposerModeUI();
    });
  });

  el('stopTurnBtn').addEventListener('click', () => {
    if (!state.activeSessionKey) return;
    sendAction({ action: 'cancel_turn', sessionKey: state.activeSessionKey });
  });

  document.querySelectorAll('.quick-chip').forEach(chip => {
    chip.addEventListener('click', () => {
      const text = chip.dataset.chip;
      if (!text) return;
      promptInput.value = text;
      resizePromptTextarea();
      promptInput.focus();
    });
  });

  promptInput.addEventListener('keydown', e => {
    if (e.key === 'Enter' && !e.shiftKey && !('ontouchstart' in window)) {
      e.preventDefault();
      el('composerForm').requestSubmit();
    }
  });

  el('composerForm').addEventListener('submit', async e => {
    e.preventDefault();
    const text = (promptInput.value || '').trim();
    if (!text) return;
    promptInput.value = '';
    resizePromptTextarea();

    if (!state.activeSessionKey) {
      await sendAction({
        action: 'create_session',
        workingDirectory: state.defaultWorkingDirectory,
        prompt: text
      });
      return;
    }

    const isBusy = state.activeSession && state.activeSession.summary && state.activeSession.summary.isTurnActive;
    let actionName = 'send_prompt';
    if (isBusy) {
      if (state.busyActionMode === 'queue') actionName = 'enqueue_prompt';
      else if (state.busyActionMode === 'steer') actionName = 'steer';
      else if (state.busyActionMode === 'interrupt') actionName = 'interrupt_and_prompt';
    }

    await sendAction({
      action: actionName,
      sessionKey: state.activeSessionKey,
      prompt: text
    });
  });

  el('newSessionForm').addEventListener('submit', async e => {
    e.preventDefault();
    const provider = el('newProviderSelect').value;
    const cwd = el('newCwdInput').value.trim();
    const model = el('newModelInput').value.trim();
    const autoApprove = el('newAutoApproveCheck').checked;
    const prompt = el('newPromptInput').value.trim();

    closeOverlays();
    el('newPromptInput').value = '';
    await sendAction({
      action: 'create_session',
      provider,
      workingDirectory: cwd,
      model: model || null,
      autoApproveTools: autoApprove,
      prompt: prompt || null
    });
  });

  async function sendAction(payload) {
    try {
      const res = await fetch('/api/action', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      if (!res.ok) return null;
      const data = await res.json();
      if (data.state) applyStateSnapshot(data.state);
      else if (data.session) applySessionDetail(data.session);
      if (!data.ok && data.message) showToast(data.message);
      return data;
    } catch {
      showToast('Network error sending action.');
      return null;
    }
  }

  function applyStateSnapshot(snap) {
    if (!snap) return;
    state.machineName = snap.machineName || 'Local PC';
    state.defaultWorkingDirectory = snap.defaultWorkingDirectory || '';
    state.scopedProvider = snap.scopedProvider || null;
    state.providers = snap.providers || [];
    state.usageSnapshots = snap.usageSnapshots || [];
    state.sessions = snap.sessions || [];
    state.recentDirectories = snap.recentDirectories || [];
    state.activeSessionKey = snap.activeSessionKey || (state.sessions[0] && state.sessions[0].sessionKey) || null;
    if (snap.activeSession) {
      state.activeSession = snap.activeSession;
    }

    machineLabel.textContent = state.scopedProvider
      ? `${state.machineName} · ${state.scopedProvider.toUpperCase()} only`
      : `${state.machineName} · All agents`;

    renderProviderFilters();
    renderSessionList();
    renderActiveSessionHeader();
    renderTimeline(true);
    renderPermissionTray();
    renderQueueTray();
    updateComposerModeUI();
    if (quotasSheet.classList.contains('open')) renderQuotasModal();
  }

  function applySessionDetail(detail) {
    if (!detail || !detail.summary) return;
    state.activeSession = detail;
    state.activeSessionKey = detail.summary.sessionKey;
    upsertSessionSummary(detail.summary);
    renderSessionList();
    renderActiveSessionHeader();
    renderTimeline(true);
    renderPermissionTray();
    renderQueueTray();
    updateComposerModeUI();
  }

  function upsertSessionSummary(summary) {
    if (!summary) return;
    const idx = state.sessions.findIndex(s => s.sessionKey === summary.sessionKey);
    if (idx >= 0) state.sessions[idx] = summary;
    else state.sessions.unshift(summary);
  }

  function handleRealtimeEnvelope(env) {
    if (!env || !env.type) return;
    if (env.type === 'state_snapshot' && env.state) {
      applyStateSnapshot(env.state);
      return;
    }
    if (env.type === 'action_error' && env.message) {
      showToast(env.message);
      return;
    }
    if (env.type === 'action_result') {
      if (env.state) applyStateSnapshot(env.state);
      else if (env.session) applySessionDetail(env.session);
      return;
    }
    if (env.type === 'session_summary' && env.summary) {
      upsertSessionSummary(env.summary);
      if (state.activeSession && state.activeSession.summary && state.activeSession.summary.sessionKey === env.summary.sessionKey) {
        state.activeSession.summary = env.summary;
        renderActiveSessionHeader();
        updateComposerModeUI();
      }
      renderSessionList();
      return;
    }
    if (env.sessionKey && env.sessionKey === state.activeSessionKey && state.activeSession) {
      if (env.type === 'timeline_item' && env.timelineItem) {
        const list = state.activeSession.timeline || [];
        const existingIdx = list.findIndex(i => i.id === env.timelineItem.id);
        if (existingIdx >= 0) list[existingIdx] = env.timelineItem;
        else list.push(env.timelineItem);
        renderTimeline(false);
      } else if (env.type === 'queue_updated' && env.queuedPrompts) {
        state.activeSession.queuedPrompts = env.queuedPrompts;
        renderQueueTray();
      } else if (env.type === 'permission_updated' && env.pendingPermissions) {
        state.activeSession.pendingPermissions = env.pendingPermissions;
        renderPermissionTray();
      }
    }
  }

  function renderProviderFilters() {
    providerFilterBar.replaceChildren();
    const counts = { all: state.sessions.length };
    state.sessions.forEach(s => {
      counts[s.provider] = (counts[s.provider] || 0) + 1;
    });

    const items = state.scopedProvider
      ? [{ id: state.scopedProvider, label: state.scopedProvider.toUpperCase() }]
      : [
          { id: 'all', label: 'All' },
          { id: 'claude', label: 'Claude' },
          { id: 'codex', label: 'Codex' },
          { id: 'grok', label: 'Grok' },
          { id: 'agy', label: 'Antigravity' }
        ];

    items.forEach(item => {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'filter-pill' + (state.providerFilter === item.id ? ' active' : '');
      const count = counts[item.id] || 0;
      btn.textContent = `${item.label} (${count})`;
      btn.addEventListener('click', () => {
        state.providerFilter = item.id;
        renderProviderFilters();
        renderSessionList();
      });
      providerFilterBar.appendChild(btn);
    });
  }

  function renderSessionList() {
    sessionList.replaceChildren();
    const filtered = state.sessions.filter(s => {
      if (state.providerFilter !== 'all' && s.provider !== state.providerFilter) return false;
      if (!state.searchQuery) return true;
      const hay = `${s.title} ${s.workingDirectory || ''} ${s.gitBranch || ''} ${s.provider}`.toLowerCase();
      return hay.includes(state.searchQuery);
    });

    if (filtered.length === 0) {
      const empty = document.createElement('div');
      empty.style.padding = '22px 14px';
      empty.style.fontSize = '12.5px';
      empty.style.color = 'var(--muted)';
      empty.style.textAlign = 'center';
      empty.textContent = 'No sessions match this filter.';
      sessionList.appendChild(empty);
      return;
    }

    filtered.forEach(s => {
      const card = document.createElement('button');
      card.type = 'button';
      card.className = 'session-card' + (s.sessionKey === state.activeSessionKey ? ' active' : '');

      const top = document.createElement('div');
      top.className = 'session-card-top';

      const pTag = document.createElement('span');
      pTag.className = `provider-tag provider-${s.provider}`;
      pTag.textContent = s.provider;
      top.appendChild(pTag);

      const statusWrap = document.createElement('div');
      statusWrap.style.display = 'flex';
      statusWrap.style.alignItems = 'center';
      statusWrap.style.gap = '6px';
      statusWrap.style.fontSize = '11px';
      statusWrap.style.color = 'var(--muted)';

      if (s.hasPendingPermission) {
        const dot = document.createElement('span');
        dot.className = 'status-dot perm';
        statusWrap.appendChild(dot);
      } else if (s.isTurnActive) {
        const dot = document.createElement('span');
        dot.className = 'status-dot busy';
        statusWrap.appendChild(dot);
      }

      if (s.queuedPromptCount > 0) {
        const qBadge = document.createElement('span');
        qBadge.style.color = 'var(--accent)';
        qBadge.style.fontWeight = '700';
        qBadge.textContent = `${s.queuedPromptCount} queued`;
        statusWrap.appendChild(qBadge);
      }

      const rel = document.createElement('span');
      rel.textContent = formatRelativeTime(s.updatedAt);
      statusWrap.appendChild(rel);
      top.appendChild(statusWrap);

      const title = document.createElement('div');
      title.className = 'session-title';
      title.textContent = s.title;

      const meta = document.createElement('div');
      meta.className = 'session-meta';
      const repo = document.createElement('span');
      repo.className = 'session-repo';
      repo.textContent = formatRepoAndBranch(s.workingDirectory, s.gitBranch);
      meta.appendChild(repo);

      card.append(top, title, meta);
      card.addEventListener('click', () => {
        closeOverlays();
        sendAction({ action: 'select_session', sessionKey: s.sessionKey });
      });
      sessionList.appendChild(card);
    });
  }

  function renderActiveSessionHeader() {
    const s = state.activeSession && state.activeSession.summary;
    if (!s) {
      activeSessionTitle.textContent = 'No active session selected';
      activeSessionMeta.textContent = 'Tap + New to launch a coding agent session';
      return;
    }

    activeSessionTitle.textContent = `[${s.provider.toUpperCase()}] ${s.title}`;
    const stateLabel = s.isTurnActive
      ? 'WORKING'
      : s.isLiveAttached
        ? 'LIVE ATTACHED'
        : 'TRANSCRIPT TAIL';
    activeSessionMeta.textContent = `${formatRepoAndBranch(s.workingDirectory, s.gitBranch)}${s.model ? ' · ' + s.model : ''} · ${stateLabel}`;
  }

  function appendFormattedMessageContent(container, rawText) {
    const codeBlockRegex = /```([a-zA-Z0-9_-]*)\n?([\s\S]*?)```/g;
    let lastIdx = 0;
    let match;

    while ((match = codeBlockRegex.exec(rawText)) !== null) {
      if (match.index > lastIdx) {
        appendInlineSegment(container, rawText.slice(lastIdx, match.index));
      }

      const lang = (match[1] || 'code').trim();
      const codeText = (match[2] || '').replace(/\n$/, '');

      const block = document.createElement('div');
      block.className = 'code-block';

      const head = document.createElement('div');
      head.className = 'code-block-head';
      const langSpan = document.createElement('span');
      langSpan.textContent = lang;
      const copyBtn = document.createElement('button');
      copyBtn.type = 'button';
      copyBtn.className = 'code-copy-btn';
      copyBtn.textContent = 'Copy';
      copyBtn.addEventListener('click', () => {
        if (navigator.clipboard) {
          navigator.clipboard.writeText(codeText).then(() => {
            copyBtn.textContent = 'Copied';
            setTimeout(() => { copyBtn.textContent = 'Copy'; }, 1400);
          }).catch(() => {});
        }
      });
      head.append(langSpan, copyBtn);

      const pre = document.createElement('pre');
      pre.textContent = codeText;
      block.append(head, pre);
      container.appendChild(block);

      lastIdx = codeBlockRegex.lastIndex;
    }

    if (lastIdx < rawText.length) {
      appendInlineSegment(container, rawText.slice(lastIdx));
    }
  }

  function appendInlineSegment(container, segmentText) {
    const trimmed = segmentText.replace(/^\n+|\n+$/g, '');
    if (!trimmed) return;

    const p = document.createElement('div');
    p.className = 'msg-text-seg';

    const parts = trimmed.split(/(`[^`\n]+`)/g);
    parts.forEach(part => {
      if (part.length > 2 && part.startsWith('`') && part.endsWith('`')) {
        const code = document.createElement('code');
        code.className = 'inline-code';
        code.textContent = part.slice(1, -1);
        p.appendChild(code);
      } else if (part) {
        p.appendChild(document.createTextNode(part));
      }
    });

    container.appendChild(p);
  }

  function renderTimeline(forceScrollBottom) {
    const nearBottom = forceScrollBottom ||
      (timelineWrap.scrollHeight - timelineWrap.scrollTop - timelineWrap.clientHeight < 120);

    timelineInner.replaceChildren();
    const items = (state.activeSession && state.activeSession.timeline) || [];

    if (items.length === 0) {
      const box = document.createElement('div');
      box.className = 'hero-card';

      const iconWrap = document.createElement('div');
      iconWrap.className = 'icon-wrap';
      const ring = document.createElement('div');
      ring.className = 'ring';
      const circle = document.createElement('div');
      circle.className = 'icon-circle';
      circle.innerHTML = '<svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="4 17 10 11 4 5"/><line x1="12" y1="19" x2="20" y2="19"/></svg>';
      iconWrap.append(ring, circle);

      const h2 = document.createElement('h2');
      h2.textContent = state.activeSessionKey ? 'Session Ready' : 'Orchestrate Your Desktop Agents';
      const p = document.createElement('p');
      p.textContent = state.activeSessionKey
        ? 'Send a prompt below to take over this session, steer it live mid-turn, or queue follow-up tasks.'
        : 'Monitor and control Claude Code, Codex, Grok, and Google Antigravity running on your PC.';

      const steps = document.createElement('div');
      steps.className = 'steps';
      const stepsTitle = document.createElement('div');
      stepsTitle.className = 'steps-title';
      stepsTitle.textContent = 'How it works';

      const s1 = document.createElement('div');
      s1.className = 'step';
      s1.innerHTML = '<span class="step-num">1</span><span>Open <strong>Sessions</strong> to inspect live transcripts or tap <strong>+ New</strong></span>';

      const s2 = document.createElement('div');
      s2.className = 'step';
      s2.innerHTML = '<span class="step-num">2</span><span>Use <strong>Queue Next</strong> to line up follow-up turns without interrupting</span>';

      const s3 = document.createElement('div');
      s3.className = 'step';
      s3.innerHTML = '<span class="step-num">3</span><span>Switch to <strong>Steer Live</strong> or <strong>Interrupt</strong> whenever you need to intervene</span>';

      steps.append(stepsTitle, s1, s2, s3);
      box.append(iconWrap, h2, p, steps);
      timelineInner.appendChild(box);
      return;
    }

    items.forEach(item => {
      if (item.kind === 'tool_call') {
        const card = document.createElement('div');
        card.className = 'tool-card';
        const header = document.createElement('div');
        header.className = 'tool-header';

        const title = document.createElement('div');
        title.className = 'tool-title';
        title.textContent = `${item.toolName || 'tool'} · ${item.text || ''}`;

        const badge = document.createElement('span');
        const isRunning = item.toolStatus === 'in_progress';
        badge.className = 'tool-status ' + (isRunning ? 'running' : 'done');
        badge.textContent = isRunning ? 'Running' : 'Done';

        header.append(title, badge);
        card.appendChild(header);

        if (item.rawInputJson || item.rawOutput) {
          const details = document.createElement('div');
          details.className = 'tool-details';
          details.textContent = [item.rawInputJson, item.rawOutput].filter(Boolean).join('\n\n');
          card.appendChild(details);
          header.addEventListener('click', () => card.classList.toggle('open'));
        }
        timelineInner.appendChild(card);
        return;
      }

      if (item.kind === 'turn_completed') {
        const row = document.createElement('div');
        row.className = 'msg-row system';
        const pill = document.createElement('div');
        pill.className = 'turn-pill';
        const parts = [`Turn ${item.stopReason || 'completed'}`];
        if (item.durationMs) parts.push(`${(item.durationMs / 1000).toFixed(1)}s`);
        if (item.inputTokens || item.outputTokens) parts.push(`${item.inputTokens || 0} in / ${item.outputTokens || 0} out tok`);
        if (item.costUsd) parts.push(`$${Number(item.costUsd).toFixed(3)}`);
        pill.textContent = parts.join(' · ');
        row.appendChild(pill);
        timelineInner.appendChild(row);
        return;
      }

      if (item.kind === 'interruption') {
        const alert = document.createElement('div');
        alert.className = 'alert-card';
        alert.textContent = `[${item.badge || 'Alert'}] ${item.text || ''}`;
        timelineInner.appendChild(alert);
        return;
      }

      if (item.kind === 'system_note') {
        const row = document.createElement('div');
        row.className = 'msg-row system';
        const pill = document.createElement('div');
        pill.className = 'turn-pill';
        pill.textContent = item.text || '';
        row.appendChild(pill);
        timelineInner.appendChild(row);
        return;
      }

      const row = document.createElement('div');
      row.className = `msg-row ${item.role === 'user' ? 'user' : 'assistant'}`;

      const bubble = document.createElement('div');
      bubble.className = 'msg-bubble';

      if (item.badge) {
        const b = document.createElement('div');
        b.className = 'msg-badge';
        b.textContent = item.badge;
        bubble.appendChild(b);
      }

      if (item.thinking) {
        const details = document.createElement('details');
        details.className = 'thinking-box';
        const summary = document.createElement('summary');
        summary.className = 'thinking-summary';
        summary.textContent = 'Agent Reasoning / Thinking';
        const body = document.createElement('div');
        body.className = 'thinking-body';
        body.textContent = item.thinking;
        details.append(summary, body);
        bubble.appendChild(details);
      }

      if (item.text) {
        const content = document.createElement('div');
        content.className = 'msg-content';
        appendFormattedMessageContent(content, item.text);
        bubble.appendChild(content);
      }

      row.appendChild(bubble);
      timelineInner.appendChild(row);
    });

    if (nearBottom) {
      timelineWrap.scrollTop = timelineWrap.scrollHeight;
    }
  }

  function renderPermissionTray() {
    const perms = (state.activeSession && state.activeSession.pendingPermissions) || [];
    if (perms.length === 0) {
      permTray.style.display = 'none';
      permTray.replaceChildren();
      return;
    }

    const p = perms[0];
    permTray.style.display = 'block';
    permTray.replaceChildren();

    const card = document.createElement('div');
    card.className = 'perm-card';

    const title = document.createElement('div');
    title.className = 'perm-title';
    title.textContent = `Approval Required · ${p.title || p.toolName}`;
    card.appendChild(title);

    if (p.description || p.rawInputJson) {
      const desc = document.createElement('div');
      desc.className = 'perm-desc';
      desc.textContent = p.description || p.rawInputJson;
      card.appendChild(desc);
    }

    const actions = document.createElement('div');
    actions.className = 'perm-actions';

    const allowBtn = document.createElement('button');
    allowBtn.type = 'button';
    allowBtn.className = 'perm-btn allow';
    allowBtn.textContent = 'Allow Tool';
    allowBtn.addEventListener('click', () => {
      sendAction({
        action: 'respond_permission',
        sessionKey: state.activeSessionKey,
        permissionRequestId: p.requestId,
        approved: true
      });
    });

    const denyBtn = document.createElement('button');
    denyBtn.type = 'button';
    denyBtn.className = 'perm-btn deny';
    denyBtn.textContent = 'Deny';
    denyBtn.addEventListener('click', () => {
      sendAction({
        action: 'respond_permission',
        sessionKey: state.activeSessionKey,
        permissionRequestId: p.requestId,
        approved: false
      });
    });

    actions.append(allowBtn, denyBtn);
    card.appendChild(actions);
    permTray.appendChild(card);
  }

  function renderQueueTray() {
    const queued = (state.activeSession && state.activeSession.queuedPrompts) || [];
    if (queued.length === 0) {
      queueTray.style.display = 'none';
      queueTray.replaceChildren();
      return;
    }

    queueTray.style.display = 'block';
    queueTray.replaceChildren();

    const inner = document.createElement('div');
    inner.className = 'queue-inner';

    const header = document.createElement('div');
    header.className = 'queue-header';
    const label = document.createElement('span');
    label.textContent = `Next-Turn Queue (${queued.length})`;
    const clearBtn = document.createElement('button');
    clearBtn.type = 'button';
    clearBtn.className = 'queue-remove-btn';
    clearBtn.style.fontSize = '11px';
    clearBtn.textContent = 'Clear All';
    clearBtn.addEventListener('click', () => {
      sendAction({ action: 'clear_queued', sessionKey: state.activeSessionKey });
    });
    header.append(label, clearBtn);
    inner.appendChild(header);

    queued.forEach((item, idx) => {
      const row = document.createElement('div');
      row.className = 'queue-item';
      const txt = document.createElement('span');
      txt.className = 'queue-item-text';
      txt.textContent = `#${idx + 1} · ${item.prompt}`;
      const rm = document.createElement('button');
      rm.type = 'button';
      rm.className = 'queue-remove-btn';
      rm.textContent = '×';
      rm.addEventListener('click', () => {
        sendAction({
          action: 'remove_queued',
          sessionKey: state.activeSessionKey,
          queueItemId: item.queueItemId
        });
      });
      row.append(txt, rm);
      inner.appendChild(row);
    });

    queueTray.appendChild(inner);
  }

  function updateComposerModeUI() {
    const isBusy = !!(state.activeSession && state.activeSession.summary && state.activeSession.summary.isTurnActive);
    busyModeBar.style.display = isBusy ? 'flex' : 'none';

    document.querySelectorAll('.mode-option').forEach(btn => {
      btn.classList.toggle('active', btn.dataset.mode === state.busyActionMode);
    });

    sendBtn.className = 'send-btn';
    if (!isBusy) {
      sendBtn.textContent = 'Send';
      promptInput.placeholder = 'Send prompt to agent…';
    } else if (state.busyActionMode === 'queue') {
      sendBtn.textContent = 'Queue';
      sendBtn.classList.add('mode-queue');
      promptInput.placeholder = 'Queue message for the next turn…';
    } else if (state.busyActionMode === 'steer') {
      sendBtn.textContent = 'Steer';
      sendBtn.classList.add('mode-steer');
      promptInput.placeholder = 'Steer the active turn live…';
    } else {
      sendBtn.textContent = 'Interrupt';
      sendBtn.classList.add('mode-interrupt');
      promptInput.placeholder = 'Stop current turn and run this instead…';
    }
  }

  function populateNewSessionModal() {
    const select = el('newProviderSelect');
    select.replaceChildren();
    const providers = state.providers.length > 0
      ? state.providers
      : [
          { shortId: 'claude', displayName: 'Claude Code', isInstalled: true },
          { shortId: 'codex', displayName: 'OpenAI Codex', isInstalled: true },
          { shortId: 'grok', displayName: 'xAI Grok', isInstalled: true },
          { shortId: 'agy', displayName: 'Google Antigravity', isInstalled: true }
        ];

    providers.forEach(p => {
      const opt = document.createElement('option');
      opt.value = p.shortId;
      opt.textContent = `${p.displayName}${p.isInstalled ? '' : ' (not detected in PATH)'}`;
      select.appendChild(opt);
    });

    const cwdInput = el('newCwdInput');
    if (!cwdInput.value) {
      const currentSessionCwd = state.activeSession && state.activeSession.summary && state.activeSession.summary.workingDirectory;
      cwdInput.value = currentSessionCwd || state.defaultWorkingDirectory || '';
    }

    const chips = el('recentDirsChips');
    chips.replaceChildren();
    state.recentDirectories.forEach(dir => {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'quick-chip';
      btn.textContent = formatRepoAndBranch(dir, null);
      btn.addEventListener('click', () => { cwdInput.value = dir; });
      chips.appendChild(btn);
    });
  }

  function renderQuotasModal() {
    const container = el('quotasList');
    container.replaceChildren();

    state.providers.forEach(p => {
      const card = document.createElement('div');
      card.className = 'quota-card';

      const top = document.createElement('div');
      top.style.display = 'flex';
      top.style.justifyContent = 'space-between';
      top.style.alignItems = 'center';

      const name = document.createElement('strong');
      name.style.fontSize = '14px';
      name.textContent = p.displayName;

      const status = document.createElement('span');
      status.style.fontSize = '11px';
      status.style.color = p.isInstalled ? 'var(--success)' : 'var(--muted)';
      status.textContent = p.isInstalled ? `Installed (${p.version || p.shortId})` : 'Not installed';

      top.append(name, status);
      card.appendChild(top);

      const usage = state.usageSnapshots.find(u => (u.provider || '').toLowerCase().startsWith(p.shortId));
      if (usage) {
        if (typeof usage.fiveHourPercentUsed === 'number') {
          const label = document.createElement('div');
          label.style.fontSize = '12px';
          label.style.color = 'var(--muted)';
          label.textContent = `5h Window: ${usage.fiveHourPercentUsed.toFixed(1)}% used`;
          const bar = document.createElement('div');
          bar.className = 'quota-bar';
          const fill = document.createElement('div');
          fill.className = 'quota-fill';
          fill.style.width = `${Math.min(100, usage.fiveHourPercentUsed)}%`;
          bar.appendChild(fill);
          card.append(label, bar);
        }
        if (usage.summaryText) {
          const sum = document.createElement('div');
          sum.style.fontSize = '11px';
          sum.style.color = 'var(--muted)';
          sum.textContent = usage.summaryText;
          card.appendChild(sum);
        }
      }

      container.appendChild(card);
    });
  }

  function formatRepoAndBranch(cwd, branch) {
    if (!cwd) return branch || 'local';
    const parts = cwd.replace(/\\/g, '/').split('/').filter(Boolean);
    const base = parts[parts.length - 1] || cwd;
    return branch ? `${base} (${branch})` : base;
  }

  function formatRelativeTime(iso) {
    if (!iso) return '';
    const diffSec = Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 1000));
    if (diffSec < 60) return 'now';
    if (diffSec < 3600) return `${Math.floor(diffSec / 60)}m`;
    if (diffSec < 86400) return `${Math.floor(diffSec / 3600)}h`;
    return `${Math.floor(diffSec / 86400)}d`;
  }

  function connectSseFallback() {
    if (state.sse) return;
    try {
      const es = new EventSource('/api/events');
      state.sse = es;
      es.onopen = () => {
        connStatusPill.innerHTML = '<span class="status-dot busy"></span>SSE';
      };
      es.onmessage = ev => {
        try {
          handleRealtimeEnvelope(JSON.parse(ev.data));
        } catch {}
      };
      es.onerror = () => {
        es.close();
        state.sse = null;
      };
    } catch {}
  }

  function connectRealtime() {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const ws = new WebSocket(`${proto}//${location.host}/ws`);
    state.ws = ws;

    ws.onopen = () => {
      if (state.sse) {
        state.sse.close();
        state.sse = null;
      }
      connStatusPill.innerHTML = '<span class="status-dot busy"></span>LIVE';
    };
    ws.onmessage = ev => {
      try {
        handleRealtimeEnvelope(JSON.parse(ev.data));
      } catch {}
    };
    ws.onclose = () => {
      connStatusPill.innerHTML = '<span class="status-dot perm"></span>SYNC';
      connectSseFallback();
      setTimeout(connectRealtime, 2000);
    };
  }

  fetch('/api/state')
    .then(r => r.json())
    .then(applyStateSnapshot)
    .catch(() => {});

  connectRealtime();
})();
""";
}
