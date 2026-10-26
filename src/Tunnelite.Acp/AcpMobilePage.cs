namespace Tunnelite.Acp;

internal static partial class AcpMobilePage
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
{{Styles}}
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

      <button id="jumpBottomBtn" type="button" class="jump-bottom-btn" style="display:none;">↓ Jump to latest</button>

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

}
