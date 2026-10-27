namespace Tunnelite.Acp;

internal static partial class AcpMobilePage
{
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
    loadingOlder: false,
    loadingSession: false,
    selectSessionReqId: 0,
    hasUnreadBelow: false,
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
  const jumpBottomBtn = el('jumpBottomBtn');
  const permTray = el('permTray');
  const queueTray = el('queueTray');
  const busyModeBar = el('busyModeBar');
  const promptInput = el('promptInput');
  const sendBtn = el('sendBtn');
  const newSessionSheet = el('newSessionSheet');
  const quotasSheet = el('quotasSheet');
  const toastBanner = el('toastBanner');
  let toastTimer = null;

  function isNearBottom() {
    return (timelineWrap.scrollHeight - timelineWrap.scrollTop - timelineWrap.clientHeight) < 150;
  }

  function updateJumpBottomButton() {
    if (!jumpBottomBtn) return;
    const items = (state.activeSession && state.activeSession.timeline) || [];
    if (items.length === 0 || isNearBottom()) {
      state.hasUnreadBelow = false;
      jumpBottomBtn.style.display = 'none';
      jumpBottomBtn.classList.remove('unread');
      return;
    }
    jumpBottomBtn.style.display = 'inline-flex';
    if (state.hasUnreadBelow) {
      jumpBottomBtn.classList.add('unread');
      jumpBottomBtn.textContent = '↓ New activity · Jump to bottom';
    } else {
      jumpBottomBtn.classList.remove('unread');
      jumpBottomBtn.textContent = '↓ Jump to latest';
    }
  }

  function scrollToBottom() {
    timelineWrap.scrollTop = timelineWrap.scrollHeight;
    requestAnimationFrame(() => {
      timelineWrap.scrollTop = timelineWrap.scrollHeight;
      state.hasUnreadBelow = false;
      updateJumpBottomButton();
    });
  }

  timelineWrap.addEventListener('scroll', () => {
    if (isNearBottom()) {
      state.hasUnreadBelow = false;
    }
    updateJumpBottomButton();
  }, { passive: true });

  if (window.visualViewport) {
    window.visualViewport.addEventListener('resize', () => {
      if (isNearBottom()) {
        scrollToBottom();
      }
    });
  }

  if (jumpBottomBtn) {
    jumpBottomBtn.addEventListener('click', () => {
      scrollToBottom();
    });
  }

  function showToast(message) {
    if (!message || !toastBanner) return;
    toastBanner.textContent = message;
    toastBanner.classList.add('show');
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(() => toastBanner.classList.remove('show'), 3800);
  }

  function resizePromptTextarea() {
    const isFocused = document.activeElement === promptInput || promptInput.classList.contains('expanded');
    promptInput.style.height = 'auto';
    if (isFocused) {
      promptInput.style.height = Math.min(Math.max(promptInput.scrollHeight, 92), 180) + 'px';
    } else {
      promptInput.style.height = '44px';
    }
  }

  promptInput.addEventListener('focus', () => {
    const wasNearBottom = isNearBottom();
    promptInput.classList.add('expanded');
    resizePromptTextarea();
    if (wasNearBottom) {
      requestAnimationFrame(() => scrollToBottom());
    }
  });

  promptInput.addEventListener('blur', () => {
    promptInput.classList.remove('expanded');
    resizePromptTextarea();
  });

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
    promptInput.classList.remove('expanded');
    promptInput.blur();
    resizePromptTextarea();

    if (!state.activeSessionKey) {
      await sendAction({
        action: 'create_session',
        workingDirectory: state.defaultWorkingDirectory,
        prompt: text
      }, { scrollMode: 'bottom' });
      scrollToBottom();
      return;
    }

    const isBusy = state.activeSession && state.activeSession.summary && state.activeSession.summary.isTurnActive;
    let actionName = 'send_prompt';
    let optimisticBadge = null;
    if (isBusy) {
      if (state.busyActionMode === 'queue') actionName = 'enqueue_prompt';
      else if (state.busyActionMode === 'steer') { actionName = 'steer'; optimisticBadge = 'STEERED'; }
      else if (state.busyActionMode === 'interrupt') { actionName = 'interrupt_and_prompt'; optimisticBadge = 'INTERRUPTED'; }
    }

    if (actionName !== 'enqueue_prompt' && state.activeSession && !timelineInner.querySelector('.hero-card')) {
      const optNode = createTimelineItemNode({
        id: 'optimistic_' + Date.now(),
        kind: 'message',
        role: 'user',
        timestamp: new Date().toISOString(),
        text: text,
        badge: optimisticBadge
      }, false);
      optNode.classList.add('optimistic-msg');
      const liveBar = timelineInner.querySelector('#liveTurnBar');
      if (liveBar) timelineInner.insertBefore(optNode, liveBar);
      else timelineInner.appendChild(optNode);
      if (state.activeSession.summary) {
        state.activeSession.summary.isTurnActive = true;
        renderActiveSessionHeader();
        updateComposerModeUI();
      }
      syncLiveTurnIndicator();
    }
    scrollToBottom();

    await sendAction({
      action: actionName,
      sessionKey: state.activeSessionKey,
      prompt: text
    }, { scrollMode: 'bottom' });
    scrollToBottom();
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

  async function sendAction(payload, options) {
    try {
      const res = await fetch('/api/action', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      if (!res.ok) return null;
      const data = await res.json();
      if (data.state) applyStateSnapshot(data.state);
      else if (data.session) applySessionDetail(data.session, options);
      if (!data.ok && data.message) showToast(data.message);
      return data;
    } catch {
      showToast('Network error sending action.');
      return null;
    }
  }

  function isSessionLive(s) {
    if (!s) return false;
    return !!(s.hasPendingPermission || s.isTurnActive || s.isActiveNow || s.isLiveAttached);
  }

  function sortSessionsInPlace() {
    state.sessions.sort((a, b) => {
      if (!!a.hasPendingPermission !== !!b.hasPendingPermission) return a.hasPendingPermission ? -1 : 1;
      if (!!a.isTurnActive !== !!b.isTurnActive) return a.isTurnActive ? -1 : 1;
      if (!!a.isActiveNow !== !!b.isActiveNow) return a.isActiveNow ? -1 : 1;
      if (!!a.isLiveAttached !== !!b.isLiveAttached) return a.isLiveAttached ? -1 : 1;
      const ta = a.updatedAt ? new Date(a.updatedAt).getTime() : 0;
      const tb = b.updatedAt ? new Date(b.updatedAt).getTime() : 0;
      return tb - ta;
    });
  }

  function applyStateSnapshot(snap) {
    if (!snap) return;
    state.machineName = snap.machineName || 'Local PC';
    state.defaultWorkingDirectory = snap.defaultWorkingDirectory || '';
    state.scopedProvider = snap.scopedProvider || null;
    state.providers = snap.providers || [];
    state.usageSnapshots = snap.usageSnapshots || [];
    state.sessions = snap.sessions || [];
    sortSessionsInPlace();
    state.recentDirectories = snap.recentDirectories || [];
    state.activeSessionKey = snap.activeSessionKey || (state.sessions[0] && state.sessions[0].sessionKey) || null;
    if (snap.activeSession) {
      state.activeSession = snap.activeSession;
    }
    state.loadingSession = false;

    machineLabel.textContent = state.scopedProvider
      ? `${state.machineName} · ${state.scopedProvider.toUpperCase()} only`
      : `${state.machineName} · All agents`;

    renderProviderFilters();
    renderSessionList();
    renderActiveSessionHeader();
    renderTimeline({ scrollMode: 'bottom' });
    renderPermissionTray();
    renderQueueTray();
    updateComposerModeUI();
    if (quotasSheet.classList.contains('open')) renderQuotasModal();
  }

  function applySessionDetail(detail, options) {
    if (!detail || !detail.summary) return;
    const prevKey = state.activeSessionKey;
    state.loadingSession = false;
    state.activeSession = detail;
    state.activeSessionKey = detail.summary.sessionKey;
    upsertSessionSummary(detail.summary);
    renderSessionList();
    renderActiveSessionHeader();
    const scrollMode = (options && options.scrollMode) ||
      (prevKey !== state.activeSessionKey ? 'bottom' : 'bottom');
    renderTimeline({
      scrollMode,
      prevScrollHeight: options && options.prevScrollHeight,
      prevScrollTop: options && options.prevScrollTop
    });
    renderPermissionTray();
    renderQueueTray();
    updateComposerModeUI();
  }

  async function selectSessionFromList(s) {
    if (!s || !s.sessionKey) return;
    closeOverlays();
    if (s.sessionKey === state.activeSessionKey && state.activeSession && !state.loadingSession) {
      scrollToBottom();
      return;
    }

    const reqId = ++state.selectSessionReqId;
    state.loadingSession = true;
    state.activeSessionKey = s.sessionKey;
    state.activeSession = {
      summary: s,
      timeline: [],
      queuedPrompts: [],
      pendingPermissions: []
    };

    renderSessionList();
    renderActiveSessionHeader();
    renderPermissionTray();
    renderQueueTray();
    updateComposerModeUI();
    renderTimeline({ scrollMode: 'bottom' });

    const res = await sendAction({ action: 'select_session', sessionKey: s.sessionKey }, { scrollMode: 'bottom' });
    if (reqId === state.selectSessionReqId && state.loadingSession) {
      state.loadingSession = false;
      renderActiveSessionHeader();
      renderTimeline({ scrollMode: 'bottom' });
    }
    return res;
  }

  function upsertSessionSummary(summary) {
    if (!summary) return;
    const idx = state.sessions.findIndex(s => s.sessionKey === summary.sessionKey);
    if (idx >= 0) state.sessions[idx] = summary;
    else state.sessions.unshift(summary);
    sortSessionsInPlace();
  }

  async function loadOlderMessages() {
    if (state.loadingOlder || !state.activeSession || !state.activeSessionKey) return;
    const items = state.activeSession.timeline || [];
    const currentMsgCount = items.filter(i => i.kind === 'message').length;
    const prevScrollHeight = timelineWrap.scrollHeight;
    const prevScrollTop = timelineWrap.scrollTop;
    state.loadingOlder = true;
    const btn = timelineInner.querySelector('.load-older-btn');
    if (btn) {
      btn.disabled = true;
      btn.textContent = 'Loading earlier messages…';
    }
    try {
      await sendAction({
        action: 'load_older',
        sessionKey: state.activeSessionKey,
        timelineLimit: Math.max(30, currentMsgCount) + 40
      }, {
        scrollMode: 'preserve',
        prevScrollHeight,
        prevScrollTop
      });
    } finally {
      state.loadingOlder = false;
    }
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
      else if (env.session && !state.loadingOlder) applySessionDetail(env.session);
      return;
    }
    if (env.type === 'session_summary' && env.summary) {
      upsertSessionSummary(env.summary);
      if (state.activeSession && state.activeSession.summary && state.activeSession.summary.sessionKey === env.summary.sessionKey) {
        const wasNearBottom = isNearBottom();
        state.activeSession.summary = env.summary;
        renderActiveSessionHeader();
        updateComposerModeUI();
        syncLiveTurnIndicator();
        if (wasNearBottom) scrollToBottom();
      }
      renderSessionList();
      return;
    }
    if (env.sessionKey && env.sessionKey === state.activeSessionKey && state.activeSession) {
      if (env.type === 'timeline_item' && env.timelineItem) {
        const wasNearBottom = isNearBottom();
        if (!state.activeSession.timeline) state.activeSession.timeline = [];
        const list = state.activeSession.timeline;
        const existingIdx = list.findIndex(i => i.id === env.timelineItem.id);
        if (existingIdx >= 0) {
          list[existingIdx] = env.timelineItem;
        } else {
          list.push(env.timelineItem);
          state.activeSession.totalTimelineCount = (state.activeSession.totalTimelineCount || list.length - 1) + 1;
        }
        upsertTimelineItemDom(env.timelineItem, wasNearBottom);
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

  function createSessionCardNode(s) {
    const live = isSessionLive(s);
    const card = document.createElement('button');
    card.type = 'button';
    card.className = 'session-card' +
      (s.sessionKey === state.activeSessionKey ? ' active' : '') +
      (live ? ' is-live' : '');

    const top = document.createElement('div');
    top.className = 'session-card-top';

    const leftTags = document.createElement('div');
    leftTags.style.display = 'flex';
    leftTags.style.alignItems = 'center';
    leftTags.style.gap = '6px';

    const pTag = document.createElement('span');
    pTag.className = `provider-tag provider-${s.provider}`;
    pTag.textContent = s.provider;
    leftTags.appendChild(pTag);

    if (s.hasPendingPermission) {
      const pill = document.createElement('span');
      pill.className = 'live-pill perm';
      const dot = document.createElement('span');
      dot.className = 'status-dot perm';
      pill.append(dot, document.createTextNode('Approval'));
      leftTags.appendChild(pill);
    } else if (s.isTurnActive) {
      const pill = document.createElement('span');
      pill.className = 'live-pill';
      const dot = document.createElement('span');
      dot.className = 'status-dot busy';
      pill.append(dot, document.createTextNode('Working'));
      leftTags.appendChild(pill);
    } else if (live) {
      const pill = document.createElement('span');
      pill.className = 'live-pill';
      const dot = document.createElement('span');
      dot.className = 'status-dot busy';
      pill.append(dot, document.createTextNode('Live'));
      leftTags.appendChild(pill);
    }

    top.appendChild(leftTags);

    const statusWrap = document.createElement('div');
    statusWrap.style.display = 'flex';
    statusWrap.style.alignItems = 'center';
    statusWrap.style.gap = '6px';
    statusWrap.style.fontSize = '11px';
    statusWrap.style.color = 'var(--muted)';

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
      selectSessionFromList(s);
    });
    return card;
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

    const liveSessions = filtered.filter(isSessionLive);
    const recentSessions = filtered.filter(s => !isSessionLive(s));

    if (liveSessions.length > 0) {
      const liveHeader = document.createElement('div');
      liveHeader.className = 'session-section-label live';
      const dot = document.createElement('span');
      dot.className = 'status-dot busy';
      liveHeader.append(dot, document.createTextNode(`Active / Live (${liveSessions.length})`));
      sessionList.appendChild(liveHeader);

      liveSessions.forEach(s => {
        sessionList.appendChild(createSessionCardNode(s));
      });
    }

    if (recentSessions.length > 0) {
      if (liveSessions.length > 0) {
        const recentHeader = document.createElement('div');
        recentHeader.className = 'session-section-label';
        recentHeader.textContent = `Recent History (${recentSessions.length})`;
        sessionList.appendChild(recentHeader);
      }

      recentSessions.forEach(s => {
        sessionList.appendChild(createSessionCardNode(s));
      });
    }
  }

  function renderActiveSessionHeader() {
    const s = state.activeSession && state.activeSession.summary;
    if (!s) {
      activeSessionTitle.textContent = 'No active session selected';
      activeSessionMeta.textContent = 'Tap + New to launch a coding agent session';
      return;
    }

    activeSessionTitle.textContent = `[${s.provider.toUpperCase()}] ${s.title}`;
    const stateLabel = state.loadingSession
      ? 'LOADING…'
      : s.isTurnActive
        ? '● WORKING'
        : (s.isActiveNow || s.isLiveAttached)
          ? '● LIVE'
          : 'READY';
    activeSessionMeta.textContent = `${formatRepoAndBranch(s.workingDirectory, s.gitBranch)}${s.model ? ' · ' + s.model : ''} · ${stateLabel}`;
  }

  function createCodeBlockNode(lang, codeText) {
    const block = document.createElement('div');
    block.className = 'code-block';

    const head = document.createElement('div');
    head.className = 'code-block-head';
    const langSpan = document.createElement('span');
    langSpan.textContent = lang || 'code';
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
    return block;
  }

  function appendFormattedMessageContent(container, rawText) {
    if (!rawText) return;
    const lines = String(rawText).replace(/\r\n/g, '\n').split('\n');
    let inFence = false;
    let fenceMarker = '';
    let fenceLang = '';
    let fenceLines = [];
    let proseLines = [];

    const flushProse = () => {
      if (proseLines.length === 0) return;
      appendMarkdownBlocks(container, proseLines);
      proseLines = [];
    };

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      if (!inFence) {
        const openMatch = line.match(/^\s*(`{3,}|~{3,})([a-zA-Z0-9_+.-]*)\s*$/);
        if (openMatch) {
          flushProse();
          inFence = true;
          fenceMarker = openMatch[1];
          fenceLang = (openMatch[2] || 'code').trim();
          fenceLines = [];
          continue;
        }
        proseLines.push(line);
      } else {
        const trimmed = line.trim();
        if (trimmed.startsWith(fenceMarker) && trimmed.replace(/[`~]/g, '') === '') {
          container.appendChild(createCodeBlockNode(fenceLang, fenceLines.join('\n')));
          inFence = false;
          fenceMarker = '';
          fenceLang = '';
          fenceLines = [];
        } else {
          fenceLines.push(line);
        }
      }
    }

    flushProse();
    if (inFence) {
      container.appendChild(createCodeBlockNode(fenceLang, fenceLines.join('\n')));
    }
  }

  function isTableSeparatorLine(line) {
    const t = line.trim();
    if (!t.includes('|')) return false;
    const cells = t.replace(/^\||\|$/g, '').split('|');
    if (cells.length === 0) return false;
    return cells.every(c => /^\s*:?-{3,}:?\s*$/.test(c));
  }

  function splitTableRow(line) {
    return line.trim().replace(/^\||\|$/g, '').split('|').map(c => c.trim());
  }

  function appendMarkdownBlocks(container, lines) {
    let i = 0;
    while (i < lines.length) {
      const line = lines[i];
      const trimmed = line.trim();

      if (!trimmed) {
        i++;
        continue;
      }

      // 1. Horizontal rule
      if (/^([-*_])(\s*\1){2,}$/.test(trimmed)) {
        const hr = document.createElement('hr');
        hr.className = 'md-hr';
        container.appendChild(hr);
        i++;
        continue;
      }

      // 2. Heading (# .. ######)
      const headingMatch = line.match(/^\s*(#{1,6})\s+(.+?)\s*#*\s*$/);
      if (headingMatch) {
        const level = Math.min(6, headingMatch[1].length);
        const h = document.createElement('div');
        h.className = `md-h${level}`;
        appendInlineMarkdown(h, headingMatch[2]);
        container.appendChild(h);
        i++;
        continue;
      }

      // 3. Pipe Table
      if (trimmed.includes('|') && i + 1 < lines.length && isTableSeparatorLine(lines[i + 1])) {
        const headers = splitTableRow(lines[i]);
        const aligns = splitTableRow(lines[i + 1]).map(c => {
          if (c.startsWith(':') && c.endsWith(':')) return 'center';
          if (c.endsWith(':')) return 'right';
          return 'left';
        });
        i += 2;

        const wrap = document.createElement('div');
        wrap.className = 'md-table-wrap';
        const table = document.createElement('table');
        table.className = 'md-table';

        const thead = document.createElement('thead');
        const trHead = document.createElement('tr');
        headers.forEach((hText, colIdx) => {
          const th = document.createElement('th');
          if (aligns[colIdx]) th.style.textAlign = aligns[colIdx];
          appendInlineMarkdown(th, hText);
          trHead.appendChild(th);
        });
        thead.appendChild(trHead);
        table.appendChild(thead);

        const tbody = document.createElement('tbody');
        while (i < lines.length && lines[i].trim().includes('|') && lines[i].trim() !== '') {
          const rowCells = splitTableRow(lines[i]);
          const tr = document.createElement('tr');
          for (let colIdx = 0; colIdx < headers.length; colIdx++) {
            const td = document.createElement('td');
            if (aligns[colIdx]) td.style.textAlign = aligns[colIdx];
            appendInlineMarkdown(td, rowCells[colIdx] || '');
            tr.appendChild(td);
          }
          tbody.appendChild(tr);
          i++;
        }
        table.appendChild(tbody);
        wrap.appendChild(table);
        container.appendChild(wrap);
        continue;
      }

      // 4. Blockquote & GitHub Alerts (> [!NOTE], etc.)
      if (/^\s*>\s?/.test(line)) {
        const quoteLines = [];
        while (i < lines.length && /^\s*>\s?/.test(lines[i])) {
          quoteLines.push(lines[i].replace(/^\s*>\s?/, ''));
          i++;
        }

        const bq = document.createElement('blockquote');
        bq.className = 'md-quote';

        if (quoteLines.length > 0) {
          const alertMatch = quoteLines[0].trim().match(/^\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*(.*)$/i);
          if (alertMatch) {
            const kind = alertMatch[1].toLowerCase();
            bq.classList.add(`alert-${kind}`);
            const label = document.createElement('div');
            label.className = 'md-alert-label';
            label.textContent = alertMatch[1].toUpperCase();
            bq.appendChild(label);
            if (alertMatch[2]) {
              quoteLines[0] = alertMatch[2];
            } else {
              quoteLines.shift();
            }
          }
        }

        const bodyText = quoteLines.join('\n').trim();
        if (bodyText) {
          const p = document.createElement('div');
          p.className = 'md-p';
          appendInlineMarkdown(p, bodyText);
          bq.appendChild(p);
        }
        container.appendChild(bq);
        continue;
      }

      // 5. Unordered or Ordered Lists
      const listMatch = line.match(/^(\s*)([-*+]|\d+[.)])\s+(.*)$/);
      if (listMatch) {
        const isOrdered = /^\d+[.)]$/.test(listMatch[2]);
        const listEl = document.createElement(isOrdered ? 'ol' : 'ul');
        listEl.className = 'md-list';

        while (i < lines.length) {
          const itemMatch = lines[i].match(/^(\s*)([-*+]|\d+[.)])\s+(.*)$/);
          if (!itemMatch) break;
          const itemOrdered = /^\d+[.)]$/.test(itemMatch[2]);
          if (itemOrdered !== isOrdered && itemMatch[1].length === 0) break;

          const li = document.createElement('li');
          if (itemMatch[1].length >= 2) {
            li.style.marginLeft = Math.min(36, itemMatch[1].length * 6) + 'px';
          }

          let itemText = itemMatch[3];
          const taskMatch = itemText.match(/^\[([ xX])\]\s+(.*)$/);
          if (taskMatch) {
            const chk = document.createElement('span');
            const isChecked = taskMatch[1].toLowerCase() === 'x';
            chk.className = 'md-task-check' + (isChecked ? ' checked' : '');
            chk.textContent = isChecked ? '✓' : '';
            li.appendChild(chk);
            itemText = taskMatch[2];
          }

          appendInlineMarkdown(li, itemText);
          listEl.appendChild(li);
          i++;
        }

        container.appendChild(listEl);
        continue;
      }

      // 6. Standard Paragraph (collect consecutive non-block lines)
      const paraLines = [];
      while (i < lines.length) {
        const l = lines[i];
        const t = l.trim();
        if (!t) break;
        if (/^([-*_])(\s*\1){2,}$/.test(t)) break;
        if (/^\s*#{1,6}\s+/.test(l)) break;
        if (/^\s*>\s?/.test(l)) break;
        if (/^(\s*)([-*+]|\d+[.)])\s+/.test(l)) break;
        if (t.includes('|') && i + 1 < lines.length && isTableSeparatorLine(lines[i + 1])) break;
        paraLines.push(l);
        i++;
      }

      if (paraLines.length > 0) {
        const p = document.createElement('p');
        p.className = 'md-p';
        appendInlineMarkdown(p, paraLines.join('\n'));
        container.appendChild(p);
      }
    }
  }

  function appendInlineMarkdown(parent, text) {
    if (!text) return;
    const tokenRegex = /(`[^`\n]+`)|(\[([^\]]+)\]\(([^)\s]+)\))|(\*\*\*([^*]+)\*\*\*)|(\*\*([^*]+)\*\*)|(__([^_]+)__)|(~~([^~]+)~~)|(\*([^*\n]+)\*)/g;
    let lastIdx = 0;
    let match;

    while ((match = tokenRegex.exec(text)) !== null) {
      if (match.index > lastIdx) {
        parent.appendChild(document.createTextNode(text.slice(lastIdx, match.index)));
      }

      if (match[1]) {
        const code = document.createElement('code');
        code.className = 'inline-code';
        code.textContent = match[1].slice(1, -1);
        parent.appendChild(code);
      } else if (match[2]) {
        const label = match[3];
        const url = match[4];
        if (/^https?:\/\//i.test(url)) {
          const a = document.createElement('a');
          a.className = 'md-link';
          a.href = url;
          a.target = '_blank';
          a.rel = 'noopener noreferrer';
          appendInlineMarkdown(a, label);
          parent.appendChild(a);
        } else {
          const chip = document.createElement('span');
          chip.className = 'md-file-chip';
          chip.title = url;
          chip.textContent = label.replace(/^`|`$/g, '');
          parent.appendChild(chip);
        }
      } else if (match[5]) {
        const strong = document.createElement('strong');
        const em = document.createElement('em');
        appendInlineMarkdown(em, match[6]);
        strong.appendChild(em);
        parent.appendChild(strong);
      } else if (match[7] || match[9]) {
        const strong = document.createElement('strong');
        appendInlineMarkdown(strong, match[8] || match[10]);
        parent.appendChild(strong);
      } else if (match[11]) {
        const del = document.createElement('del');
        appendInlineMarkdown(del, match[12]);
        parent.appendChild(del);
      } else if (match[13]) {
        const em = document.createElement('em');
        appendInlineMarkdown(em, match[14]);
        parent.appendChild(em);
      }

      lastIdx = tokenRegex.lastIndex;
    }

    if (lastIdx < text.length) {
      parent.appendChild(document.createTextNode(text.slice(lastIdx)));
    }
  }

  function formatMessageClock(iso) {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (isNaN(d.getTime())) return '';
      return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    } catch {
      return '';
    }
  }

  function getActiveProviderLabel() {
    const s = state.activeSession && state.activeSession.summary;
    if (!s) return 'AGENT';
    return (s.providerDisplayName || s.provider || 'Agent').toUpperCase();
  }

  const thoughtStreamers = new Map();

  function computeThoughtWindowChars() {
    const w = window.innerWidth || 390;
    return Math.max(44, Math.min(108, Math.floor((w - 118) / 6.8)));
  }

  function extractLatestThoughtParagraph(rawText) {
    if (!rawText) return '';
    const cleaned = String(rawText).trim();
    const lastNl = cleaned.lastIndexOf('\n');
    const latest = lastNl >= 0 ? cleaned.slice(lastNl + 1).trim() : cleaned;
    return latest || cleaned.replace(/\n+/g, ' ').trim();
  }

  function formatThoughtLineWindow(rawText, windowChars) {
    const latest = extractLatestThoughtParagraph(rawText);
    if (!latest) return '';
    const limit = windowChars || computeThoughtWindowChars();
    if (latest.length <= limit) return latest;
    const words = latest.split(/\s+/).filter(Boolean);
    let currentLine = '';
    for (let i = 0; i < words.length; i++) {
      const w = words[i].length <= limit ? words[i] : words[i].slice(-limit);
      if (!currentLine) {
        currentLine = w;
      } else if (currentLine.length + 1 + w.length <= limit) {
        currentLine += ' ' + w;
      } else {
        currentLine = w;
      }
    }
    return currentLine || latest.slice(-limit);
  }

  function stopAllThoughtStreamersExcept(activeItemId) {
    thoughtStreamers.forEach((st, id) => {
      if (id !== activeItemId && st.timer) {
        clearInterval(st.timer);
        st.timer = null;
      }
    });
  }

  function bindThoughtRowStream(item, isRunning, thoughtRow, thoughtSpan, rawThoughtText) {
    const windowChars = computeThoughtWindowChars();
    thoughtRow.dataset.fullThought = rawThoughtText;

    if (!isRunning) {
      const prev = thoughtStreamers.get(item.id);
      if (prev && prev.timer) {
        clearInterval(prev.timer);
        prev.timer = null;
      }
      thoughtSpan.textContent = thoughtRow.classList.contains('wrap')
        ? rawThoughtText
        : formatThoughtLineWindow(rawThoughtText, windowChars);
      return;
    }

    stopAllThoughtStreamersExcept(item.id);
    const latestPara = extractLatestThoughtParagraph(rawThoughtText);
    const words = latestPara ? latestPara.split(/\s+/).filter(Boolean) : [];
    let st = thoughtStreamers.get(item.id);

    if (!st || st.paragraph !== latestPara) {
      const isPrefixExtension = st && st.paragraph && latestPara.startsWith(st.paragraph);
      if (isPrefixExtension) {
        st.paragraph = latestPara;
        st.words = words;
      } else {
        if (st && st.timer) clearInterval(st.timer);
        st = {
          paragraph: latestPara,
          words,
          wordIndex: 0,
          currentLine: '',
          timer: null,
          rowEl: thoughtRow,
          spanEl: thoughtSpan
        };
        thoughtStreamers.set(item.id, st);
      }
    }

    st.rowEl = thoughtRow;
    st.spanEl = thoughtSpan;

    const advanceWord = () => {
      const limit = computeThoughtWindowChars();
      if (st.wordIndex >= st.words.length) {
        if (st.timer) {
          clearInterval(st.timer);
          st.timer = null;
        }
        return;
      }
      const nextWord = st.words[st.wordIndex++];
      const clipped = nextWord.length <= limit ? nextWord : nextWord.slice(-limit);
      if (!st.currentLine) {
        st.currentLine = clipped;
      } else if (st.currentLine.length + 1 + clipped.length <= limit) {
        st.currentLine += ' ' + clipped;
      } else {
        st.currentLine = clipped;
      }
      if (st.rowEl && st.spanEl) {
        st.spanEl.textContent = st.rowEl.classList.contains('wrap')
          ? (st.rowEl.dataset.fullThought || latestPara)
          : st.currentLine;
      }
    };

    if (!st.currentLine && st.words.length > 0) {
      advanceWord();
    } else {
      thoughtSpan.textContent = thoughtRow.classList.contains('wrap')
        ? rawThoughtText
        : (st.currentLine || formatThoughtLineWindow(rawThoughtText, windowChars));
    }

    if (!st.timer && st.wordIndex < st.words.length) {
      const remaining = st.words.length - st.wordIndex;
      const stepMs = remaining > 24 ? 16 : 28;
      st.timer = setInterval(advanceWord, stepMs);
    }
  }

  function createTimelineItemNode(item, isLatestItem) {
    if (item.kind === 'tool_call') {
      const isRunning = item.toolStatus === 'in_progress' && !!isLatestItem;
      const cmdLogs = (item.activityLog && item.activityLog.length > 0) ? item.activityLog : [];
      const cmdCount = cmdLogs.length;
      const hasCommand = cmdCount > 0 || !!(item.toolName && item.toolName !== 'thinking' && item.toolKind !== 'thought');
      const thoughtText = (item.thinking || (!hasCommand ? item.text : '') || '').trim();
      const hasThought = thoughtText.length > 0;

      const card = document.createElement('div');
      card.className = 'tool-card' +
        (isRunning ? ' running' : '') +
        (!hasCommand && hasThought ? ' thought-mode' : '');
      card.dataset.itemId = item.id;

      if (hasThought) {
        const thoughtRow = document.createElement('div');
        thoughtRow.className = 'tool-thought-row';

        const thoughtTitle = document.createElement('div');
        thoughtTitle.className = 'tool-title';

        const thoughtGlyph = document.createElement('span');
        thoughtGlyph.className = 'tool-prompt-glyph thought';
        thoughtGlyph.textContent = '✻';
        thoughtTitle.appendChild(thoughtGlyph);

        const thoughtChip = document.createElement('span');
        thoughtChip.className = 'tool-name-chip thought';
        thoughtChip.textContent = 'thinking';
        thoughtTitle.appendChild(thoughtChip);

        const thoughtSpan = document.createElement('span');
        thoughtSpan.className = 'tool-target-text thought';
        thoughtTitle.appendChild(thoughtSpan);

        bindThoughtRowStream(item, isRunning, thoughtRow, thoughtSpan, thoughtText);

        thoughtRow.appendChild(thoughtTitle);

        if (!hasCommand) {
          const thoughtRight = document.createElement('div');
          thoughtRight.className = 'tool-right';
          const badge = document.createElement('span');
          badge.className = 'tool-status ' + (isRunning ? 'running' : 'done');
          badge.textContent = isRunning ? 'Running' : 'Done';
          thoughtRight.appendChild(badge);
          thoughtRow.appendChild(thoughtRight);
        }

        thoughtRow.addEventListener('click', e => {
          e.stopPropagation();
          const wrapped = thoughtRow.classList.toggle('wrap');
          const full = thoughtRow.dataset.fullThought || thoughtText;
          thoughtSpan.textContent = wrapped
            ? full
            : formatThoughtLineWindow(full, computeThoughtWindowChars());
        });

        card.appendChild(thoughtRow);
      }

      if (hasCommand) {
        const header = document.createElement('div');
        header.className = 'tool-header' + (cmdCount > 0 ? ' expandable' : '');

        const title = document.createElement('div');
        title.className = 'tool-title';

        const glyph = document.createElement('span');
        glyph.className = 'tool-prompt-glyph' + (isRunning ? ' running' : '');
        glyph.textContent = '❯';
        title.appendChild(glyph);

        const toolName = (item.toolName || 'tool').trim();
        const nameChip = document.createElement('span');
        nameChip.className = 'tool-name-chip';
        nameChip.textContent = toolName;
        title.appendChild(nameChip);

        const rawTarget = (item.text || '').trim();
        if (rawTarget && rawTarget.toLowerCase() !== toolName.toLowerCase()) {
          const targetSpan = document.createElement('span');
          targetSpan.className = 'tool-target-text';
          targetSpan.textContent = rawTarget;
          title.appendChild(targetSpan);
        }

        const rightWrap = document.createElement('div');
        rightWrap.className = 'tool-right';

        if (cmdCount > 0) {
          const stepPill = document.createElement('span');
          stepPill.className = 'tool-step-count';
          stepPill.textContent = `${cmdCount} ${cmdCount === 1 ? 'cmd' : 'cmds'}`;
          rightWrap.appendChild(stepPill);
        }

        const badge = document.createElement('span');
        badge.className = 'tool-status ' + (isRunning ? 'running' : 'done');
        badge.textContent = isRunning ? 'Running' : 'Done';
        rightWrap.appendChild(badge);

        if (cmdCount > 0) {
          const chev = document.createElement('span');
          chev.className = 'tool-chevron';
          chev.textContent = '▸';
          rightWrap.appendChild(chev);
        }

        header.append(title, rightWrap);
        card.appendChild(header);

        if (cmdCount > 0) {
          const details = document.createElement('div');
          details.className = 'tool-details';

          const logBox = document.createElement('div');
          logBox.className = 'tool-log-list';
          cmdLogs.forEach((entry, idx) => {
            const line = document.createElement('div');
            line.className = 'tool-log-line';

            const num = document.createElement('span');
            num.className = 'tool-log-num';
            num.textContent = String(idx + 1).padStart(2, '0');

            const rowGlyph = document.createElement('span');
            rowGlyph.className = 'tool-log-glyph';
            rowGlyph.textContent = '❯';

            const sepIdx = entry.indexOf(' · ');
            const cName = sepIdx >= 0 ? entry.slice(0, sepIdx) : entry;
            const cTarget = sepIdx >= 0 ? entry.slice(sepIdx + 3) : '';

            const chip = document.createElement('span');
            chip.className = 'tool-log-chip';
            chip.textContent = cName;

            line.append(num, rowGlyph, chip);

            if (cTarget) {
              const targetEl = document.createElement('span');
              targetEl.className = 'tool-log-target';
              targetEl.textContent = cTarget;
              line.appendChild(targetEl);
            }

            line.addEventListener('click', e => {
              e.stopPropagation();
              line.classList.toggle('wrap');
            });
            logBox.appendChild(line);
          });

          details.appendChild(logBox);
          card.appendChild(details);
          header.addEventListener('click', () => card.classList.toggle('open'));
        }
      }
      return card;
    }

    if (item.kind === 'turn_completed') {
      const row = document.createElement('div');
      row.className = 'msg-row system';
      row.dataset.itemId = item.id;
      const pill = document.createElement('div');
      pill.className = 'turn-pill';
      const parts = [`Turn ${item.stopReason || 'completed'}`];
      if (item.durationMs) parts.push(`${(item.durationMs / 1000).toFixed(1)}s`);
      if (item.inputTokens || item.outputTokens) parts.push(`${item.inputTokens || 0} in / ${item.outputTokens || 0} out tok`);
      if (item.costUsd) parts.push(`$${Number(item.costUsd).toFixed(3)}`);
      pill.textContent = parts.join(' · ');
      row.appendChild(pill);
      return row;
    }

    if (item.kind === 'interruption') {
      const alert = document.createElement('div');
      alert.className = 'alert-card';
      alert.dataset.itemId = item.id;
      alert.textContent = `[${item.badge || 'Alert'}] ${item.text || ''}`;
      return alert;
    }

    if (item.kind === 'system_note') {
      const row = document.createElement('div');
      row.className = 'msg-row system';
      row.dataset.itemId = item.id;
      const pill = document.createElement('div');
      pill.className = 'turn-pill';
      pill.textContent = item.text || '';
      row.appendChild(pill);
      return row;
    }

    const isUser = item.role === 'user';
    const row = document.createElement('div');
    row.className = `msg-row ${isUser ? 'user' : 'assistant'}`;
    row.dataset.itemId = item.id;

    const bubble = document.createElement('div');
    bubble.className = 'msg-bubble';

    const metaRow = document.createElement('div');
    metaRow.className = 'msg-meta-row';
    const metaLeft = document.createElement('div');
    metaLeft.className = 'msg-meta-left';

    const roleSpan = document.createElement('span');
    roleSpan.className = `msg-role-label ${isUser ? 'user' : 'assistant'}`;
    roleSpan.textContent = isUser ? 'YOU' : getActiveProviderLabel();
    metaLeft.appendChild(roleSpan);

    if (item.badge) {
      const b = document.createElement('span');
      b.className = 'msg-badge';
      b.textContent = item.badge;
      metaLeft.appendChild(b);
    }

    const timeSpan = document.createElement('span');
    timeSpan.className = 'msg-time';
    timeSpan.textContent = formatMessageClock(item.timestamp);
    metaRow.append(metaLeft, timeSpan);
    bubble.appendChild(metaRow);

    if (item.text) {
      const content = document.createElement('div');
      content.className = 'msg-content';
      appendFormattedMessageContent(content, item.text);
      const isTurnActive = !!(state.activeSession && state.activeSession.summary && state.activeSession.summary.isTurnActive);
      if (!isUser && isLatestItem && isTurnActive) {
        const cursor = document.createElement('span');
        cursor.className = 'stream-cursor';
        cursor.textContent = '▍';
        const lastBlock = content.lastElementChild;
        if (lastBlock && lastBlock.tagName !== 'DIV') {
          lastBlock.appendChild(cursor);
        } else {
          content.appendChild(cursor);
        }
      }
      bubble.appendChild(content);
    }

    row.appendChild(bubble);
    return row;
  }

  function syncLiveTurnIndicator() {
    const existingBar = timelineInner.querySelector('#liveTurnBar');
    const summary = state.activeSession && state.activeSession.summary;
    const isTurnActive = !!(summary && summary.isTurnActive);
    const items = (state.activeSession && state.activeSession.timeline) || [];
    const lastItem = items.length > 0 ? items[items.length - 1] : null;
    const hasEmptyHero = !state.loadingSession && !!timelineInner.querySelector('.hero-card');

    if (!isTurnActive || hasEmptyHero) {
      if (existingBar) existingBar.remove();
      return;
    }

    const labelText = (lastItem && lastItem.kind === 'message' && lastItem.role === 'assistant')
      ? `${getActiveProviderLabel()} is streaming response live…`
      : `${getActiveProviderLabel()} is working live on desktop…`;

    if (existingBar) {
      const txt = existingBar.querySelector('.live-turn-text');
      if (txt) txt.textContent = labelText;
      timelineInner.appendChild(existingBar);
      return;
    }

    const bar = document.createElement('div');
    bar.id = 'liveTurnBar';
    bar.className = 'live-turn-bar';
    const left = document.createElement('div');
    left.className = 'live-turn-left';
    const dot = document.createElement('span');
    dot.className = 'status-dot busy';
    const txt = document.createElement('span');
    txt.className = 'live-turn-text';
    txt.textContent = labelText;
    left.append(dot, txt);
    const right = document.createElement('span');
    right.textContent = 'LIVE';
    right.style.fontSize = '10px';
    right.style.fontWeight = '700';
    bar.append(left, right);
    timelineInner.appendChild(bar);
  }

  function markPreviousToolCardsDone() {
    timelineInner.querySelectorAll('.tool-card.running').forEach(card => {
      card.classList.remove('running');
      const glyph = card.querySelector('.tool-prompt-glyph.running');
      if (glyph) glyph.classList.remove('running');
      const status = card.querySelector('.tool-status.running');
      if (status) {
        status.className = 'tool-status done';
        status.textContent = 'Done';
      }
    });
  }

  function upsertTimelineItemDom(item, wasNearBottom) {
    if (timelineInner.querySelector('.hero-card')) {
      renderTimeline({ scrollMode: 'bottom' });
      return;
    }

    if (item.kind === 'message' && item.role === 'user') {
      timelineInner.querySelectorAll('.optimistic-msg').forEach(el => el.remove());
    }

    timelineInner.querySelectorAll('.stream-cursor').forEach(el => el.remove());

    let existingNode = null;
    const children = timelineInner.children;
    for (let i = children.length - 1; i >= 0; i--) {
      if (children[i].dataset && children[i].dataset.itemId === item.id) {
        existingNode = children[i];
        break;
      }
    }

    const newNode = createTimelineItemNode(item, true);
    if (existingNode) {
      if (existingNode.classList.contains('open')) {
        newNode.classList.add('open');
      }
      const prevThoughtRow = existingNode.querySelector('.tool-thought-row');
      const nextThoughtRow = newNode.querySelector('.tool-thought-row');
      if (prevThoughtRow && nextThoughtRow && prevThoughtRow.classList.contains('wrap')) {
        nextThoughtRow.classList.add('wrap');
        const nextSpan = nextThoughtRow.querySelector('.tool-target-text.thought');
        if (nextSpan && nextThoughtRow.dataset.fullThought) {
          nextSpan.textContent = nextThoughtRow.dataset.fullThought;
        }
      }
      const prevDetails = existingNode.querySelector('details.thinking-box');
      const nextDetails = newNode.querySelector('details.thinking-box');
      if (prevDetails && nextDetails && prevDetails.open) {
        nextDetails.open = true;
      }
      existingNode.replaceWith(newNode);
    } else {
      markPreviousToolCardsDone();
      const liveBar = timelineInner.querySelector('#liveTurnBar');
      if (liveBar) {
        timelineInner.insertBefore(newNode, liveBar);
      } else {
        timelineInner.appendChild(newNode);
      }
    }

    syncLiveTurnIndicator();

    if (wasNearBottom) {
      scrollToBottom();
    } else {
      state.hasUnreadBelow = true;
      updateJumpBottomButton();
    }
  }

  function renderTimeline(options) {
    const scrollMode = typeof options === 'boolean'
      ? (options ? 'bottom' : 'auto')
      : ((options && options.scrollMode) || 'auto');
    const wasNearBottom = scrollMode === 'bottom' || isNearBottom();

    timelineInner.replaceChildren();
    const items = (state.activeSession && state.activeSession.timeline) || [];

    if (state.loadingSession) {
      const box = document.createElement('div');
      box.className = 'hero-card';

      const iconWrap = document.createElement('div');
      iconWrap.className = 'icon-wrap';
      const ring = document.createElement('div');
      ring.className = 'ring loading';
      const circle = document.createElement('div');
      circle.className = 'icon-circle';
      circle.innerHTML = '<svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="4 17 10 11 4 5"/><line x1="12" y1="19" x2="20" y2="19"/></svg>';
      iconWrap.append(ring, circle);

      const h2 = document.createElement('h2');
      h2.textContent = 'Loading session messages…';
      const p = document.createElement('p');
      const s = state.activeSession && state.activeSession.summary;
      p.textContent = s
        ? `[${(s.provider || 'agent').toUpperCase()}] ${s.title}`
        : 'Reading session transcript…';

      box.append(iconWrap, h2, p);
      timelineInner.appendChild(box);
      syncLiveTurnIndicator();
      updateJumpBottomButton();
      return;
    }

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
      updateJumpBottomButton();
      return;
    }

    if (state.activeSession && state.activeSession.hasOlderMessages) {
      const bar = document.createElement('div');
      bar.className = 'load-older-bar';
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'load-older-btn';
      const hiddenCount = state.activeSession.hiddenOlderCount || 0;
      const visibleMsgCount = items.filter(i => i.kind === 'message').length || items.length;
      const totalCount = state.activeSession.totalTimelineCount || (visibleMsgCount + hiddenCount);
      btn.textContent = `↑ Load earlier messages (${hiddenCount} older · showing last ${visibleMsgCount} of ${totalCount} messages)`;
      btn.addEventListener('click', loadOlderMessages);
      bar.appendChild(btn);
      timelineInner.appendChild(bar);
    }

    items.forEach((item, idx) => {
      timelineInner.appendChild(createTimelineItemNode(item, idx === items.length - 1));
    });

    syncLiveTurnIndicator();

    if (scrollMode === 'preserve' && options && typeof options.prevScrollHeight === 'number') {
      const heightDelta = timelineWrap.scrollHeight - options.prevScrollHeight;
      timelineWrap.scrollTop = (options.prevScrollTop || 0) + Math.max(0, heightDelta);
      updateJumpBottomButton();
    } else if (wasNearBottom) {
      scrollToBottom();
    } else {
      updateJumpBottomButton();
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
