namespace Tunnelite.Acp;

internal static partial class AcpMobilePage
{
    private const string Styles = """
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
    .session-card.is-live {
      border-left: 3px solid var(--success);
      background: linear-gradient(90deg, rgba(34, 197, 94, 0.09) 0%, rgba(15, 23, 42, 0.65) 55%);
    }
    .session-card.is-live.active {
      background: linear-gradient(90deg, rgba(34, 197, 94, 0.14) 0%, rgba(56, 189, 248, 0.12) 60%);
      border-color: var(--border-strong);
      border-left: 3px solid var(--success);
    }
    .session-section-label {
      font-size: 10px;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 0.08em;
      color: var(--muted);
      padding: 8px 8px 4px;
      display: flex;
      align-items: center;
      gap: 6px;
    }
    .session-section-label.live {
      color: #4ade80;
    }
    .live-pill {
      display: inline-flex;
      align-items: center;
      gap: 5px;
      padding: 2px 7px;
      border-radius: 999px;
      font-size: 10px;
      font-weight: 700;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      background: rgba(34, 197, 94, 0.14);
      color: #4ade80;
      border: 1px solid rgba(34, 197, 94, 0.3);
    }
    .live-pill.perm {
      background: rgba(245, 158, 11, 0.16);
      color: #fbbf24;
      border-color: rgba(245, 158, 11, 0.35);
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
      position: relative;
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
      overscroll-behavior-y: contain;
    }
    .timeline-inner {
      width: 100%;
      max-width: 820px;
      margin: 0 auto;
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    .load-older-bar {
      display: flex;
      justify-content: center;
      padding: 2px 0 6px;
    }
    .load-older-btn {
      min-height: 36px;
      padding: 6px 16px;
      border-radius: 999px;
      border: 1px solid var(--border-strong);
      background: rgba(56, 189, 248, 0.08);
      color: var(--accent);
      font-size: 12px;
      font-weight: 600;
      display: inline-flex;
      align-items: center;
      gap: 6px;
      cursor: pointer;
      transition: background 0.15s ease, transform 0.1s ease;
    }
    .load-older-btn:hover {
      background: rgba(56, 189, 248, 0.16);
    }
    .load-older-btn:active {
      transform: scale(0.97);
    }
    .load-older-btn:disabled {
      opacity: 0.55;
      cursor: default;
    }
    .jump-bottom-btn {
      align-self: center;
      margin: -44px auto 8px;
      z-index: 25;
      min-height: 34px;
      padding: 6px 14px;
      border-radius: 999px;
      border: 1px solid var(--border-strong);
      background: rgba(15, 23, 42, 0.92);
      backdrop-filter: blur(12px);
      color: var(--accent);
      font-size: 12px;
      font-weight: 600;
      box-shadow: 0 8px 24px rgba(0, 0, 0, 0.45);
      display: inline-flex;
      align-items: center;
      gap: 6px;
      cursor: pointer;
    }
    .jump-bottom-btn.unread {
      background: rgba(34, 197, 94, 0.18);
      border-color: rgba(34, 197, 94, 0.45);
      color: #4ade80;
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
    .msg-row.assistant { align-items: flex-start; width: 100%; }
    .msg-row.system { align-items: center; }
    .msg-bubble {
      max-width: 88%;
      padding: 11px 14px;
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
      max-width: 100%;
      width: 100%;
    }
    .msg-meta-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
      margin-bottom: 6px;
      font-family: var(--mono);
      font-size: 10.5px;
      color: var(--muted);
    }
    .msg-meta-left {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      font-weight: 700;
      letter-spacing: 0.04em;
      text-transform: uppercase;
    }
    .msg-role-label.user { color: #7dd3fc; }
    .msg-role-label.assistant { color: #cbd5e1; }
    .msg-time {
      font-size: 10px;
      color: var(--muted);
      opacity: 0.85;
    }
    .msg-badge {
      display: inline-flex;
      align-items: center;
      font-size: 9.5px;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      padding: 1px 6px;
      border-radius: 999px;
      background: rgba(56, 189, 248, 0.14);
      color: var(--accent);
      border: 1px solid rgba(56, 189, 248, 0.26);
    }
    .stream-cursor {
      display: inline-block;
      margin-left: 3px;
      color: var(--accent);
      font-family: var(--mono);
      font-weight: 700;
      animation: pulse 0.9s infinite ease-in-out;
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
      font-size: 12.5px;
      color: var(--muted);
      max-height: 240px;
      overflow-y: auto;
      line-height: 1.55;
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .msg-content {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .msg-text-seg, .md-p {
      margin: 0;
      white-space: pre-wrap;
      line-height: 1.62;
    }
    .md-h1, .md-h2, .md-h3, .md-h4, .md-h5, .md-h6 {
      font-weight: 700;
      letter-spacing: -0.015em;
      color: #f8fafc;
      line-height: 1.35;
      margin: 4px 0 0;
    }
    .md-h1 {
      font-size: 17px;
      padding-bottom: 5px;
      border-bottom: 1px solid var(--border);
    }
    .md-h2 {
      font-size: 15.5px;
      padding-bottom: 4px;
      border-bottom: 1px solid rgba(148, 163, 184, 0.12);
    }
    .md-h3 { font-size: 14.5px; }
    .md-h4, .md-h5, .md-h6 { font-size: 13.5px; color: var(--text-soft); }
    .md-list {
      margin: 0;
      padding-left: 20px;
      display: flex;
      flex-direction: column;
      gap: 4px;
      line-height: 1.58;
    }
    .md-list .md-list {
      margin-top: 4px;
    }
    .md-list li {
      padding-left: 2px;
    }
    .md-task-check {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 15px;
      height: 15px;
      border-radius: 4px;
      font-size: 10px;
      font-weight: 700;
      margin-right: 6px;
      vertical-align: text-bottom;
      border: 1px solid var(--border-strong);
      background: rgba(15, 23, 42, 0.6);
      color: var(--muted);
    }
    .md-task-check.checked {
      background: rgba(34, 197, 94, 0.18);
      border-color: rgba(34, 197, 94, 0.45);
      color: #4ade80;
    }
    .md-quote {
      margin: 0;
      padding: 8px 12px;
      border-left: 3px solid var(--accent);
      background: rgba(15, 23, 42, 0.55);
      border-radius: 0 10px 10px 0;
      color: var(--text-soft);
      font-size: 13.5px;
      display: flex;
      flex-direction: column;
      gap: 4px;
    }
    .md-quote.alert-note { border-left-color: #38bdf8; background: rgba(56, 189, 248, 0.08); }
    .md-quote.alert-tip { border-left-color: #22c55e; background: rgba(34, 197, 94, 0.08); }
    .md-quote.alert-important { border-left-color: #a855f7; background: rgba(168, 85, 247, 0.08); }
    .md-quote.alert-warning { border-left-color: #f59e0b; background: rgba(245, 158, 11, 0.08); }
    .md-quote.alert-caution { border-left-color: #ef4444; background: rgba(239, 68, 68, 0.08); }
    .md-alert-label {
      font-size: 11px;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 0.06em;
    }
    .md-hr {
      border: 0;
      height: 1px;
      background: var(--border);
      margin: 4px 0;
    }
    .md-link {
      color: var(--accent);
      text-decoration: underline;
      text-underline-offset: 2px;
    }
    .md-file-chip {
      font-family: var(--mono);
      font-size: 12px;
      padding: 1px 6px;
      border-radius: 6px;
      background: rgba(56, 189, 248, 0.11);
      border: 1px solid rgba(56, 189, 248, 0.28);
      color: #7dd3fc;
    }
    .md-table-wrap {
      width: 100%;
      overflow-x: auto;
      border-radius: 10px;
      border: 1px solid var(--border);
      background: rgba(2, 6, 23, 0.55);
    }
    .md-table {
      width: 100%;
      border-collapse: collapse;
      font-size: 12.5px;
      line-height: 1.45;
    }
    .md-table th, .md-table td {
      padding: 7px 10px;
      border-bottom: 1px solid var(--border);
      text-align: left;
    }
    .md-table th {
      background: rgba(15, 23, 42, 0.85);
      font-weight: 700;
      color: #f8fafc;
      white-space: nowrap;
    }
    .md-table tr:last-child td {
      border-bottom: 0;
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
    /* In-Line Terminal Activity Bar */
    .tool-card {
      width: 100%;
      border-radius: 11px;
      border: 1px solid rgba(56, 189, 248, 0.2);
      background: rgba(2, 6, 23, 0.88);
      box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.04);
      overflow: hidden;
      transition: border-color 0.2s ease, box-shadow 0.2s ease;
    }
    .tool-card.running {
      border-color: rgba(56, 189, 248, 0.45);
      box-shadow: 0 0 16px rgba(56, 189, 248, 0.12), inset 0 1px 0 rgba(255, 255, 255, 0.06);
    }
    .tool-card.thought-mode {
      border-color: rgba(192, 132, 252, 0.32);
    }
    .tool-thought-row {
      padding: 7px 12px;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
      font-size: 12px;
      background: rgba(192, 132, 252, 0.05);
      cursor: pointer;
      user-select: none;
    }
    .tool-thought-row + .tool-header {
      border-top: 1px solid rgba(148, 163, 184, 0.12);
    }
    .tool-thought-row.wrap .tool-title {
      align-items: flex-start;
    }
    .tool-thought-row.wrap .tool-target-text.thought {
      white-space: pre-wrap;
      word-break: break-word;
    }
    .ring.loading {
      border-color: rgba(56, 189, 248, 0.16);
      border-top-color: var(--accent);
      animation: spinRing 0.9s linear infinite;
    }
    @keyframes spinRing {
      to { transform: rotate(360deg); }
    }
    .tool-header {
      padding: 8px 12px;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
      font-size: 12px;
      user-select: none;
    }
    .tool-header.expandable {
      cursor: pointer;
    }
    .tool-title {
      display: flex;
      align-items: center;
      gap: 7px;
      min-width: 0;
      flex: 1;
      font-family: var(--mono);
      font-weight: 600;
      color: var(--text-soft);
    }
    .tool-prompt-glyph {
      color: #4ade80;
      font-weight: 700;
      font-size: 12px;
      flex-shrink: 0;
    }
    .tool-prompt-glyph.running {
      color: var(--accent);
      animation: pulse 1s infinite ease-in-out;
    }
    .tool-prompt-glyph.thought {
      color: #c084fc;
      animation: pulse 1.1s infinite ease-in-out;
    }
    .tool-name-chip {
      padding: 2px 7px;
      border-radius: 6px;
      background: rgba(56, 189, 248, 0.11);
      border: 1px solid rgba(56, 189, 248, 0.26);
      color: #7dd3fc;
      font-size: 11px;
      font-weight: 700;
      flex-shrink: 0;
    }
    .tool-name-chip.thought {
      background: rgba(192, 132, 252, 0.14);
      border-color: rgba(192, 132, 252, 0.3);
      color: #d8b4fe;
    }
    .tool-target-text {
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      color: #cbd5e1;
      font-weight: 500;
      font-size: 11.5px;
    }
    .tool-target-text.thought {
      font-family: var(--font);
      font-style: italic;
      color: #cbd5e1;
    }
    .tool-right {
      display: flex;
      align-items: center;
      gap: 6px;
      flex-shrink: 0;
    }
    .tool-chevron {
      font-family: var(--mono);
      font-size: 11px;
      color: var(--muted);
      transition: transform 0.15s ease;
      display: inline-block;
    }
    .tool-card.open .tool-chevron {
      transform: rotate(90deg);
      color: var(--accent);
    }
    .tool-step-count {
      font-family: var(--mono);
      font-size: 10.5px;
      font-weight: 600;
      color: var(--muted);
      padding: 2px 6px;
      border-radius: 6px;
      background: rgba(148, 163, 184, 0.1);
      border: 1px solid rgba(148, 163, 184, 0.18);
    }
    .tool-status {
      font-size: 10px;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      padding: 2px 7px;
      border-radius: 999px;
      flex-shrink: 0;
    }
    .tool-status.running { background: rgba(56, 189, 248, 0.14); color: var(--accent); border: 1px solid rgba(56, 189, 248, 0.25); }
    .tool-status.done { background: rgba(34, 197, 94, 0.14); color: var(--success); border: 1px solid rgba(34, 197, 94, 0.25); }
    .tool-details {
      display: none;
      padding: 8px 12px;
      border-top: 1px solid var(--border);
      background: rgba(2, 6, 23, 0.94);
      font-family: var(--mono);
      font-size: 11.5px;
      color: var(--muted);
      max-height: 240px;
      overflow-y: auto;
      line-height: 1.45;
    }
    .tool-card.open .tool-details {
      display: flex;
      flex-direction: column;
    }
    .tool-log-list {
      display: flex;
      flex-direction: column;
      gap: 5px;
    }
    .tool-log-line {
      display: flex;
      align-items: center;
      gap: 7px;
      min-width: 0;
      cursor: pointer;
    }
    .tool-log-num {
      color: #64748b;
      font-size: 10.5px;
      flex-shrink: 0;
    }
    .tool-log-glyph {
      color: #4ade80;
      font-weight: 700;
      font-size: 11px;
      flex-shrink: 0;
    }
    .tool-log-chip {
      padding: 1px 6px;
      border-radius: 5px;
      background: rgba(56, 189, 248, 0.1);
      border: 1px solid rgba(56, 189, 248, 0.22);
      color: #7dd3fc;
      font-size: 10.5px;
      font-weight: 600;
      flex-shrink: 0;
    }
    .tool-log-target {
      color: #cbd5e1;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      min-width: 0;
      flex: 1;
    }
    .tool-log-line.wrap {
      align-items: flex-start;
    }
    .tool-log-line.wrap .tool-log-target {
      white-space: pre-wrap;
      word-break: break-word;
    }
    .live-turn-bar {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
      padding: 8px 12px;
      border-radius: 11px;
      border: 1px dashed rgba(34, 197, 94, 0.35);
      background: rgba(2, 6, 23, 0.72);
      font-family: var(--mono);
      font-size: 11.5px;
      color: #4ade80;
    }
    .live-turn-left {
      display: flex;
      align-items: center;
      gap: 8px;
      min-width: 0;
    }
    .live-turn-text {
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
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
      max-height: 180px;
      padding: 10px 14px;
      border-radius: 12px;
      border: 1px solid var(--border);
      background: var(--surface-2);
      font-size: 15px;
      line-height: 1.45;
      resize: none;
      outline: none;
      transition: min-height 0.16s ease, height 0.16s ease, border-color 0.15s ease, box-shadow 0.15s ease;
    }
    .prompt-textarea:focus,
    .prompt-textarea.expanded {
      min-height: 92px;
      max-height: 180px;
      border-color: var(--border-strong);
      box-shadow: 0 0 0 2px rgba(56, 189, 248, 0.14);
    }
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
""";
}
