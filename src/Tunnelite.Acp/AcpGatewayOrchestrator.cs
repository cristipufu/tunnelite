using System.Collections.Concurrent;
using Acplite.Sdk;
using Acplite.Sdk.Protocol;
using Acplite.Sdk.Runtime;

namespace Tunnelite.Acp;

/// <summary>
/// Orchestrates local coding agent discovery, live transcript tailing, interactive session takeover,
/// mid-turn steering, FIFO next-turn prompt queueing, and tool permission approvals for mobile clients.
/// Uses <see cref="SemaphoreSlim"/> exclusively for async coordination.
/// </summary>
public sealed class AcpGatewayOrchestrator : IAsyncDisposable
{
    private readonly AcpliteRuntime _runtime;
    private readonly bool _ownsRuntime;
    private readonly AgentProviderKind? _scopedProvider;
    private readonly string _defaultWorkingDirectory;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, AcpManagedSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<AgentProviderKind, AcpUsageSnapshot> _usageByProvider = new();
    private readonly ConcurrentDictionary<long, Func<AcpGatewayRealtimeEnvelope, Task>> _subscribers = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task? _monitorTask;

    private volatile IReadOnlyList<AcpProviderDescriptor> _providers = [];
    private volatile string? _activeSessionKey;
    private long _subscriberIdCounter;
    private long _timelineItemCounter;
    private int _isDisposed;

    public AcpGatewayOrchestrator(
        AgentProviderKind? scopedProvider = null,
        string? defaultWorkingDirectory = null,
        AcpliteRuntime? runtime = null)
    {
        _scopedProvider = scopedProvider;
        _defaultWorkingDirectory = !string.IsNullOrWhiteSpace(defaultWorkingDirectory)
            ? Path.GetFullPath(defaultWorkingDirectory)
            : Environment.CurrentDirectory;
        _ownsRuntime = runtime is null;
        _runtime = runtime ?? new AcpliteRuntime(externalSessionBridge: PtySessionBridge.Instance);
    }

    public AgentProviderKind? ScopedProvider => _scopedProvider;
    public string DefaultWorkingDirectory => _defaultWorkingDirectory;

    public IDisposable Subscribe(Func<AcpGatewayRealtimeEnvelope, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var id = Interlocked.Increment(ref _subscriberIdCounter);
        _subscribers[id] = callback;
        return new SubscriptionToken(_subscribers, id);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RefreshDiscoveryCoreAsync(refreshProviders: true, cancellationToken).ConfigureAwait(false);
        _monitorTask ??= Task.Run(() => MonitorActiveSessionsLoopAsync(_lifetimeCts.Token), _lifetimeCts.Token);
    }

    public Task RefreshDiscoveryAsync(CancellationToken cancellationToken = default)
        => RefreshDiscoveryCoreAsync(refreshProviders: _providers.Count == 0, cancellationToken);

    private async Task RefreshDiscoveryCoreAsync(bool refreshProviders, CancellationToken cancellationToken)
    {
        if (refreshProviders || _providers.Count == 0)
        {
            _providers = await _runtime.DiscoverProvidersAsync(
                providerFilter: _scopedProvider,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var discoveredSessions = await _runtime.DiscoverSessionsAsync(
            providerFilter: _scopedProvider,
            maxSessionsPerProvider: AcpGatewayDefaults.MaxDiscoveredSessionsPerProvider,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MergeDiscoveredSessionsUnderStateGateAsync(discoveredSessions, cancellationToken).ConfigureAwait(false);
            EnsureActiveSessionSelectedUnderStateGate();
        }
        finally
        {
            _stateGate.Release();
        }

        if (_activeSessionKey is { } activeKey && _sessions.TryGetValue(activeKey, out var activeSession))
        {
            await EnsureTailingStartedAsync(activeSession, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MergeDiscoveredSessionsUnderStateGateAsync(
        IReadOnlyList<AcpDiscoveredSession> discoveredSessions,
        CancellationToken cancellationToken)
    {
        foreach (var discovered in discoveredSessions)
        {
            var key = AcpTextFormatter.BuildSessionKey(discovered.Provider, discovered.SessionId);
            if (!_sessions.TryGetValue(key, out var existing))
            {
                _sessions[key] = AcpManagedSession.FromDiscovered(discovered, _defaultWorkingDirectory);
                continue;
            }

            await existing.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                existing.UpdateFromDiscoveryUnderGate(discovered);
            }
            finally
            {
                existing.Gate.Release();
            }
        }
    }

    private void EnsureActiveSessionSelectedUnderStateGate()
    {
        if (!string.IsNullOrEmpty(_activeSessionKey) && _sessions.ContainsKey(_activeSessionKey))
        {
            return;
        }

        AcpManagedSession? mostRecent = null;
        foreach (var (_, candidate) in _sessions)
        {
            if (mostRecent is null || candidate.UpdatedAt > mostRecent.UpdatedAt)
            {
                mostRecent = candidate;
            }
        }

        _activeSessionKey = mostRecent?.SessionKey;
    }

    public async Task ProbeAllUsageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshots = await _runtime.ProbeUsageAsync(
                providerFilter: _scopedProvider,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var snapshot in snapshots)
            {
                _usageByProvider[snapshot.Provider] = snapshot;
            }
        }
        catch
        {
            // Ignore provider probe error
        }

        await BroadcastStateAsync().ConfigureAwait(false);
    }

    public async Task<AcpGatewayStateSnapshot> GetStateSnapshotAsync(
        string? requestedSessionKey = null,
        CancellationToken cancellationToken = default)
    {
        var targetKey = !string.IsNullOrWhiteSpace(requestedSessionKey)
            ? requestedSessionKey
            : _activeSessionKey;

        AcpGatewaySessionDetail? activeDetail = null;
        if (!string.IsNullOrWhiteSpace(targetKey) && _sessions.TryGetValue(targetKey, out var activeSession))
        {
            await EnsureTailingStartedAsync(activeSession, cancellationToken).ConfigureAwait(false);
            activeDetail = await activeSession.BuildDetailAsync(cancellationToken).ConfigureAwait(false);
        }

        var (summaries, recentDirectories) = await CollectSessionSummariesAndDirectoriesAsync(cancellationToken).ConfigureAwait(false);

        return new AcpGatewayStateSnapshot
        {
            MachineName = Environment.MachineName,
            DefaultWorkingDirectory = _defaultWorkingDirectory,
            ScopedProvider = _scopedProvider?.ToShortId(),
            Providers = _providers,
            UsageSnapshots = CollectUsageSnapshots(),
            Sessions = summaries,
            RecentDirectories = recentDirectories,
            ActiveSessionKey = targetKey,
            ActiveSession = activeDetail
        };
    }

    private async Task<(List<AcpGatewaySessionSummary> Summaries, string[] RecentDirectories)> CollectSessionSummariesAndDirectoriesAsync(
        CancellationToken cancellationToken)
    {
        var summaries = new List<AcpGatewaySessionSummary>();
        var recentDirs = new HashSet<string>(StringComparer.Ordinal) { _defaultWorkingDirectory };

        foreach (var (_, session) in _sessions)
        {
            summaries.Add(await session.BuildSummaryAsync(cancellationToken).ConfigureAwait(false));
            if (!string.IsNullOrWhiteSpace(session.WorkingDirectory))
            {
                recentDirs.Add(session.WorkingDirectory!);
            }
        }

        summaries.Sort(CompareSessionSummaries);
        return (summaries, recentDirs.Take(AcpGatewayDefaults.MaxRecentDirectories).ToArray());
    }

    private static int CompareSessionSummaries(AcpGatewaySessionSummary a, AcpGatewaySessionSummary b)
    {
        var permCompare = b.HasPendingPermission.CompareTo(a.HasPendingPermission);
        if (permCompare != 0) return permCompare;
        var activeTurnCompare = b.IsTurnActive.CompareTo(a.IsTurnActive);
        if (activeTurnCompare != 0) return activeTurnCompare;
        var activeNowCompare = b.IsActiveNow.CompareTo(a.IsActiveNow);
        if (activeNowCompare != 0) return activeNowCompare;
        var attachedCompare = b.IsLiveAttached.CompareTo(a.IsLiveAttached);
        if (attachedCompare != 0) return attachedCompare;
        return b.UpdatedAt.CompareTo(a.UpdatedAt);
    }

    private List<AcpUsageSnapshot> CollectUsageSnapshots()
    {
        var usages = new List<AcpUsageSnapshot>();
        foreach (var (_, usage) in _usageByProvider)
        {
            usages.Add(usage);
        }
        return usages;
    }

    public async Task<AcpGatewayActionResponse> ExecuteActionAsync(
        AcpGatewayActionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await DispatchActionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AcpGatewayActionResponse
            {
                Ok = false,
                Message = ex.Message
            };
        }
    }

    private Task<AcpGatewayActionResponse> DispatchActionAsync(
        AcpGatewayActionRequest request,
        CancellationToken cancellationToken)
    {
        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
        return action switch
        {
            GatewayActions.SelectSession => HandleSelectSessionAsync(request, cancellationToken),
            GatewayActions.LoadOlder => HandleLoadOlderAsync(request, cancellationToken),
            GatewayActions.RefreshSessions => HandleRefreshSessionsAsync(request, cancellationToken),
            GatewayActions.ProbeUsage => HandleProbeUsageAsync(request, cancellationToken),
            GatewayActions.CreateSession => HandleCreateSessionAsync(request, cancellationToken),
            GatewayActions.SendPrompt => HandleSendPromptAsync(request, cancellationToken),
            GatewayActions.Steer => HandleSteerAsync(request, cancellationToken),
            GatewayActions.EnqueuePrompt => HandleEnqueuePromptAsync(request, cancellationToken),
            GatewayActions.RemoveQueued => HandleRemoveQueuedAsync(request, cancellationToken),
            GatewayActions.ClearQueued => HandleClearQueuedAsync(request, cancellationToken),
            GatewayActions.InterruptAndPrompt => HandleInterruptAndPromptAsync(request, cancellationToken),
            GatewayActions.CancelTurn => HandleCancelTurnAsync(request, cancellationToken),
            GatewayActions.RespondPermission => HandleRespondPermissionAsync(request, cancellationToken),
            GatewayActions.SetModel => HandleSetModelAsync(request, cancellationToken),
            _ => Task.FromResult(new AcpGatewayActionResponse { Ok = false, Message = $"Unknown action '{request.Action}'." })
        };
    }

    private async Task<AcpGatewayActionResponse> HandleSelectSessionAsync(
        AcpGatewayActionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SessionKey) ||
            !_sessions.TryGetValue(request.SessionKey, out var session))
        {
            return new AcpGatewayActionResponse { Ok = false, Message = "Session not found." };
        }

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session.VisibleTimelineLimit = request.TimelineLimit is > 0
                ? request.TimelineLimit.Value
                : AcpGatewayDefaults.DefaultTimelinePageSize;
        }
        finally
        {
            session.Gate.Release();
        }

        _activeSessionKey = session.SessionKey;
        await EnsureTailingStartedAsync(session, cancellationToken).ConfigureAwait(false);
        return new AcpGatewayActionResponse
        {
            Ok = true,
            SessionKey = session.SessionKey,
            Session = await session.BuildDetailAsync(cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<AcpGatewayActionResponse> HandleLoadOlderAsync(
        AcpGatewayActionRequest request,
        CancellationToken cancellationToken)
    {
        var session = await ResolveTargetSessionAsync(request.SessionKey, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return new AcpGatewayActionResponse { Ok = false, Message = "Session not found." };
        }

        await EnsureTailingStartedAsync(session, cancellationToken).ConfigureAwait(false);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session.VisibleTimelineLimit = request.TimelineLimit is > 0
                ? request.TimelineLimit.Value
                : session.VisibleTimelineLimit + AcpGatewayDefaults.TimelinePageStep;
        }
        finally
        {
            session.Gate.Release();
        }

        return new AcpGatewayActionResponse
        {
            Ok = true,
            SessionKey = session.SessionKey,
            Session = await session.BuildDetailAsync(cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<AcpGatewayActionResponse> HandleRefreshSessionsAsync(
        AcpGatewayActionRequest request,
        CancellationToken cancellationToken)
    {
        await RefreshDiscoveryCoreAsync(refreshProviders: true, cancellationToken).ConfigureAwait(false);
        var state = await GetStateSnapshotAsync(request.SessionKey, cancellationToken).ConfigureAwait(false);
        await BroadcastStateAsync().ConfigureAwait(false);
        return new AcpGatewayActionResponse { Ok = true, State = state };
    }

    private async Task<AcpGatewayActionResponse> HandleProbeUsageAsync(
        AcpGatewayActionRequest request,
        CancellationToken cancellationToken)
    {
        await ProbeAllUsageAsync(cancellationToken).ConfigureAwait(false);
        var state = await GetStateSnapshotAsync(request.SessionKey, cancellationToken).ConfigureAwait(false);
        return new AcpGatewayActionResponse { Ok = true, State = state };
    }

    private async Task<AcpGatewayActionResponse> HandleCreateSessionAsync(
        AcpGatewayActionRequest request,
        CancellationToken cancellationToken)
    {
        var providerKind = _scopedProvider ??
            (AgentProviderParser.TryParse(request.Provider, out var parsed) ? parsed : AgentProviderKind.Claude);

        var cwd = !string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? request.WorkingDirectory.Trim()
            : _defaultWorkingDirectory;

        var session = await CreateInteractiveSessionAsync(
            providerKind,
            cwd,
            request.Model,
            request.AutoApproveTools ?? false,
            request.Prompt,
            cancellationToken).ConfigureAwait(false);

        _activeSessionKey = session.SessionKey;
        var state = await GetStateSnapshotAsync(session.SessionKey, cancellationToken).ConfigureAwait(false);
        await BroadcastStateAsync().ConfigureAwait(false);

        return new AcpGatewayActionResponse
        {
            Ok = true,
            SessionKey = session.SessionKey,
            State = state,
            Session = state.ActiveSession
        };
    }

    private async Task<AcpGatewayActionResponse> HandleSendPromptAsync(
        AcpGatewayActionRequest request,
        CancellationToken cancellationToken)
    {
        var session = await ResolveTargetSessionAsync(request.SessionKey, cancellationToken).ConfigureAwait(false);
        if (session is null || string.IsNullOrWhiteSpace(request.Prompt))
        {
            return new AcpGatewayActionResponse { Ok = false, Message = "Select a session and enter a prompt." };
        }

        var prompt = request.Prompt.Trim();
        var handle = await EnsureLiveHandleAttachedAsync(session, cancellationToken).ConfigureAwait(false);
        await AppendUserPromptItemAsync(session, prompt, badge: null, cancellationToken).ConfigureAwait(false);
        await handle.SendPromptAsync(prompt, cancellationToken).ConfigureAwait(false);
        await BroadcastSessionSummaryAsync(session).ConfigureAwait(false);

        return new AcpGatewayActionResponse
        {
            Ok = true,
            SessionKey = session.SessionKey,
            Session = await session.BuildDetailAsync(cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<AcpGatewayActionResponse> HandleSteerAsync(
        AcpGatewayActionRequest request,
        CancellationToken cancellationToken)
    {
        var session = await ResolveTargetSessionAsync(request.SessionKey, cancellationToken).ConfigureAwait(false);
        if (session is null || string.IsNullOrWhiteSpace(request.Prompt))
        {
            return new AcpGatewayActionResponse { Ok = false, Message = "Select a session and enter steering instructions." };
        }

        var prompt = request.Prompt.Trim();
        var handle = await EnsureLiveHandleAttachedAsync(session, cancellationToken).ConfigureAwait(false);
        await AppendUserPromptItemAsync(session, prompt, badge: TimelineBadges.Steered, cancellationToken).ConfigureAwait(false);
        var accepted = await handle.SteerAsync(prompt, cancellationToken).ConfigureAwait(false);
        await BroadcastSessionSummaryAsync(session).ConfigureAwait(false);

        return new AcpGatewayActionResponse
        {
            Ok = accepted,
            Message = accepted ? "Live steering injected." : "Steering was not accepted by the provider.",
            SessionKey = session.SessionKey,
            Session = await session.BuildDetailAsync(cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<AcpGatewayActionResponse> HandleEnqueuePromptAsync(
        AcpGatewayActionRequest request,
        CancellationToken cancellationToken)
    {
        var session = await ResolveTargetSessionAsync(request.SessionKey, cancellationToken).ConfigureAwait(false);
        if (session is null || string.IsNullOrWhiteSpace(request.Prompt))
        {
            return new AcpGatewayActionResponse { Ok = false, Message = "Select a session and enter a prompt to queue." };
        }

        var prompt = request.Prompt.Trim();
        var handle = await EnsureLiveHandleAttachedAsync(session, cancellationToken).ConfigureAwait(false);
        if (!handle.IsTurnActive)
        {
            await AppendUserPromptItemAsync(session, prompt, badge: null, cancellationToken).ConfigureAwait(false);
        }

        var queuedItem = await handle.EnqueuePromptAsync(prompt, cancellationToken).ConfigureAwait(false);
        await BroadcastSessionSummaryAsync(session).ConfigureAwait(false);

        return new AcpGatewayActionResponse
        {
            Ok = true,
            Message = queuedItem is not null ? "Prompt queued for next turn." : "Session was idle; prompt sent immediately.",
            SessionKey = session.SessionKey,
            QueuedItem = queuedItem,
            Session = await session.BuildDetailAsync(cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<AcpGatewayActionResponse> HandleRemoveQueuedAsync(
        AcpGatewayActionRequest request,
        CancellationToken cancellationToken)
    {
        var session = await ResolveTargetSessionAsync(request.SessionKey, cancellationToken).ConfigureAwait(false);
        if (session?.LiveHandle is null || string.IsNullOrWhiteSpace(request.QueueItemId))
        {
            return new AcpGatewayActionResponse { Ok = false, Message = "Queued item not found." };
        }

        var removed = await session.LiveHandle.RemoveQueuedPromptAsync(request.QueueItemId, cancellationToken).ConfigureAwait(false);
        await BroadcastSessionSummaryAsync(session).ConfigureAwait(false);

        return new AcpGatewayActionResponse
        {
            Ok = removed,
            SessionKey = session.SessionKey,
            Session = await session.BuildDetailAsync(cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<AcpGatewayActionResponse> HandleClearQueuedAsync(
        AcpGatewayActionRequest request,
        CancellationToken cancellationToken)
    {
        var session = await ResolveTargetSessionAsync(request.SessionKey, cancellationToken).ConfigureAwait(false);
        if (session?.LiveHandle is not null)
        {
            await session.LiveHandle.ClearQueuedPromptsAsync(cancellationToken).ConfigureAwait(false);
            await BroadcastSessionSummaryAsync(session).ConfigureAwait(false);
        }

        return new AcpGatewayActionResponse
        {
            Ok = true,
            SessionKey = session?.SessionKey,
            Session = session is not null ? await session.BuildDetailAsync(cancellationToken).ConfigureAwait(false) : null
        };
    }

    private async Task<AcpGatewayActionResponse> HandleInterruptAndPromptAsync(
        AcpGatewayActionRequest request,
        CancellationToken cancellationToken)
    {
        var session = await ResolveTargetSessionAsync(request.SessionKey, cancellationToken).ConfigureAwait(false);
        if (session is null || string.IsNullOrWhiteSpace(request.Prompt))
        {
            return new AcpGatewayActionResponse { Ok = false, Message = "Select a session and enter an interrupting prompt." };
        }

        var prompt = request.Prompt.Trim();
        var handle = await EnsureLiveHandleAttachedAsync(session, cancellationToken).ConfigureAwait(false);
        await AppendUserPromptItemAsync(session, prompt, badge: TimelineBadges.Interrupt, cancellationToken).ConfigureAwait(false);
        await handle.InterruptAndSendPromptAsync(prompt, cancellationToken).ConfigureAwait(false);
        await BroadcastSessionSummaryAsync(session).ConfigureAwait(false);

        return new AcpGatewayActionResponse
        {
            Ok = true,
            Message = "Interrupted current turn and started new prompt.",
            SessionKey = session.SessionKey,
            Session = await session.BuildDetailAsync(cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<AcpGatewayActionResponse> HandleCancelTurnAsync(
        AcpGatewayActionRequest request,
        CancellationToken cancellationToken)
    {
        var session = await ResolveTargetSessionAsync(request.SessionKey, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return new AcpGatewayActionResponse { Ok = false, Message = "No active live session to stop." };
        }

        var handle = session.LiveHandle ?? await TryAttachLiveHandleQuietlyAsync(session, cancellationToken).ConfigureAwait(false);
        if (handle is null)
        {
            return new AcpGatewayActionResponse { Ok = false, Message = "No active live session to stop." };
        }

        await handle.CancelTurnAsync(cancellationToken).ConfigureAwait(false);
        await AppendSystemNoteAsync(session, "Turn interrupted from mobile.", cancellationToken).ConfigureAwait(false);
        await BroadcastSessionSummaryAsync(session).ConfigureAwait(false);

        return new AcpGatewayActionResponse
        {
            Ok = true,
            Message = "Interrupt signal sent.",
            SessionKey = session.SessionKey,
            Session = await session.BuildDetailAsync(cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<AcpGatewayActionResponse> HandleRespondPermissionAsync(
        AcpGatewayActionRequest request,
        CancellationToken cancellationToken)
    {
        var session = await ResolveTargetSessionAsync(request.SessionKey, cancellationToken).ConfigureAwait(false);
        if (session is null || string.IsNullOrWhiteSpace(request.PermissionRequestId))
        {
            return new AcpGatewayActionResponse { Ok = false, Message = "Permission request is no longer active." };
        }

        var handle = session.LiveHandle ?? await TryAttachLiveHandleQuietlyAsync(session, cancellationToken).ConfigureAwait(false);
        if (handle is null)
        {
            return new AcpGatewayActionResponse { Ok = false, Message = "Permission request is no longer active." };
        }

        var approved = request.Approved ?? true;
        await handle.RespondPermissionAsync(
            request.PermissionRequestId,
            approved,
            request.SelectedOptionId,
            cancellationToken).ConfigureAwait(false);

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AcpGatewayPendingPermission> remainingPermissions;
        try
        {
            session.PendingPermissions.RemoveAll(
                p => string.Equals(p.RequestId, request.PermissionRequestId, StringComparison.Ordinal));
            remainingPermissions = session.PendingPermissions.ToArray();
        }
        finally
        {
            session.Gate.Release();
        }

        await AppendSystemNoteAsync(
            session,
            approved ? "Tool execution approved from mobile." : "Tool execution denied from mobile.",
            cancellationToken).ConfigureAwait(false);

        await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
        {
            Type = RealtimeEnvelopeTypes.PermissionUpdated,
            SessionKey = session.SessionKey,
            PendingPermissions = remainingPermissions
        }).ConfigureAwait(false);
        await BroadcastSessionSummaryAsync(session).ConfigureAwait(false);

        return new AcpGatewayActionResponse
        {
            Ok = true,
            SessionKey = session.SessionKey,
            Session = await session.BuildDetailAsync(cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<AcpGatewayActionResponse> HandleSetModelAsync(
        AcpGatewayActionRequest request,
        CancellationToken cancellationToken)
    {
        var session = await ResolveTargetSessionAsync(request.SessionKey, cancellationToken).ConfigureAwait(false);
        if (session is null || string.IsNullOrWhiteSpace(request.Model))
        {
            return new AcpGatewayActionResponse { Ok = false, Message = "Select a session and specify a model." };
        }

        var modelId = request.Model.Trim();
        IAcpSessionHandle? handle;
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session.Model = modelId;
            handle = session.LiveHandle;
        }
        finally
        {
            session.Gate.Release();
        }

        if (handle is not null)
        {
            await handle.SetModelAsync(modelId, cancellationToken).ConfigureAwait(false);
        }
        await BroadcastSessionSummaryAsync(session).ConfigureAwait(false);

        return new AcpGatewayActionResponse
        {
            Ok = true,
            Message = $"Model updated to {modelId}.",
            SessionKey = session.SessionKey,
            Session = await session.BuildDetailAsync(cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<AcpManagedSession?> ResolveTargetSessionAsync(
        string? sessionKey,
        CancellationToken cancellationToken)
    {
        var key = !string.IsNullOrWhiteSpace(sessionKey) ? sessionKey : _activeSessionKey;
        if (!string.IsNullOrWhiteSpace(key) && _sessions.TryGetValue(key, out var found))
        {
            return found;
        }

        await RefreshDiscoveryAsync(cancellationToken).ConfigureAwait(false);
        key = !string.IsNullOrWhiteSpace(sessionKey) ? sessionKey : _activeSessionKey;
        return !string.IsNullOrWhiteSpace(key) && _sessions.TryGetValue(key, out var afterRefresh)
            ? afterRefresh
            : null;
    }

    private async Task<AcpManagedSession> CreateInteractiveSessionAsync(
        AgentProviderKind provider,
        string workingDirectory,
        string? model,
        bool autoApproveTools,
        string? initialPrompt,
        CancellationToken cancellationToken)
    {
        var handle = await _runtime.StartSessionAsync(new AcpSessionConfig
        {
            Provider = provider,
            WorkingDirectory = workingDirectory,
            ResumeExisting = false,
            Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            AutoApproveTools = autoApproveTools
        }, cancellationToken).ConfigureAwait(false);

        var session = AcpManagedSession.FromInteractive(
            provider,
            handle,
            workingDirectory,
            model,
            autoApproveTools,
            initialPrompt);

        _sessions[session.SessionKey] = session;
        StartLiveHandlePump(session, handle);

        if (!string.IsNullOrWhiteSpace(initialPrompt))
        {
            var trimmed = initialPrompt.Trim();
            await AppendUserPromptItemAsync(session, trimmed, badge: null, cancellationToken).ConfigureAwait(false);
            await handle.SendPromptAsync(trimmed, cancellationToken).ConfigureAwait(false);
        }

        return session;
    }

    private async Task<IAcpSessionHandle?> TryAttachLiveHandleQuietlyAsync(
        AcpManagedSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            return await EnsureLiveHandleAttachedAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task<IAcpSessionHandle> EnsureLiveHandleAttachedAsync(
        AcpManagedSession session,
        CancellationToken cancellationToken)
    {
        await session.AttachGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DetachedLiveHandle staleHandle;
            AcpSessionConfig config;
            AcpDiscoveredSession? discoveredForPty = null;

            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (session.LiveHandle is not null && !session.HasExternalTranscriptActivitySinceAttach)
                {
                    return session.LiveHandle;
                }

                staleHandle = session.DetachLiveHandleUnderGate();
                config = session.BuildAttachConfigUnderGate(_defaultWorkingDirectory);
                if (!string.IsNullOrWhiteSpace(session.TranscriptPath))
                {
                    discoveredForPty = session.ToDiscoveredSessionUnderGate();
                }
            }
            finally
            {
                session.Gate.Release();
            }

            await staleHandle.DisposeAsync().ConfigureAwait(false);

            IAcpSessionHandle? ptyHandle = null;
            if (config.AttachToPtyIfAvailable && (config.ResumeExisting || !string.IsNullOrWhiteSpace(config.SessionId)))
            {
                ptyHandle = await _runtime.TryAttachToExternalSessionAsync(
                    config,
                    discoveredForPty,
                    enableInternalTranscriptTail: false,
                    cancellationToken).ConfigureAwait(false);
            }

            var handle = ptyHandle ?? await _runtime.StartSessionAsync(config, cancellationToken).ConfigureAwait(false);

            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                session.LiveHandle = handle;
                session.HasMobileAttachedOnce = true;
                session.HasExternalTranscriptActivitySinceAttach = false;
                if (handle is not PtyAttachedSessionHandle)
                {
                    session.MarkMobileTurnStartingUnderGate(DateTimeOffset.UtcNow);
                }
                StartLiveHandlePump(session, handle);
            }
            finally
            {
                session.Gate.Release();
            }

            var actualKey = AcpTextFormatter.BuildSessionKey(session.Provider, handle.SessionId);
            if (!string.Equals(actualKey, session.SessionKey, StringComparison.Ordinal))
            {
                _sessions[actualKey] = session;
            }

            return handle;
        }
        finally
        {
            session.AttachGate.Release();
        }
    }

    private async Task EnsureTailingStartedAsync(AcpManagedSession session, CancellationToken cancellationToken)
    {
        await session.AttachGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var discoveredForHistory = await TryPrepareInitialHistoryLoadAsync(session, cancellationToken).ConfigureAwait(false);
            if (discoveredForHistory is not null)
            {
                await LoadInitialTranscriptHistoryAsync(session, discoveredForHistory, cancellationToken).ConfigureAwait(false);
            }

            await StartBackgroundTranscriptTailerIfNeededAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.AttachGate.Release();
        }
    }

    private static async Task<AcpDiscoveredSession?> TryPrepareInitialHistoryLoadAsync(
        AcpManagedSession session,
        CancellationToken cancellationToken)
    {
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrWhiteSpace(session.TranscriptPath) || !File.Exists(session.TranscriptPath))
            {
                return null;
            }

            return !session.InitialHistoryLoaded && session.LiveHandle is null
                ? session.ToDiscoveredSessionUnderGate()
                : null;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task LoadInitialTranscriptHistoryAsync(
        AcpManagedSession session,
        AcpDiscoveredSession discoveredForHistory,
        CancellationToken cancellationToken)
    {
        var historicalEvents = new List<AcpEvent>();
        try
        {
            var adapter = _runtime.GetAdapter(session.Provider);
            await foreach (var ev in adapter.TailSessionTranscriptAsync(
                discoveredForHistory,
                includeHistory: true,
                followLive: false,
                cancellationToken).ConfigureAwait(false))
            {
                historicalEvents.Add(ev);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Ignore malformed transcript history lines
        }

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.InitialHistoryLoaded || session.LiveHandle is not null)
            {
                return;
            }

            foreach (var ev in historicalEvents)
            {
                AcpTimelineProjector.ApplyEventUnderGate(session, ev, isHistoricalLoad: true, NextItemId);
            }

            session.InitialHistoryLoaded = true;
            RecordTranscriptFileMetricsUnderGate(session, discoveredForHistory.TranscriptPath);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private static void RecordTranscriptFileMetricsUnderGate(AcpManagedSession session, string transcriptPath)
    {
        try
        {
            var fi = new FileInfo(transcriptPath);
            if (!fi.Exists)
            {
                return;
            }

            session.LastObservedFileLength = fi.Length;
            var lastWrite = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero);
            session.ReconcileInitialHistoryActivityUnderGate(lastWrite);
        }
        catch
        {
            // Ignore file stat error
        }
    }

    private async Task StartBackgroundTranscriptTailerIfNeededAsync(
        AcpManagedSession session,
        CancellationToken cancellationToken)
    {
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.TailTask is not null ||
                string.IsNullOrWhiteSpace(session.TranscriptPath) ||
                !File.Exists(session.TranscriptPath))
            {
                return;
            }

            var tailCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            session.TailCts = tailCts;
            var discovered = session.ToDiscoveredSessionUnderGate();
            session.TailTask = Task.Run(() => PumpTranscriptTailAsync(session, discovered, tailCts.Token), tailCts.Token);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task PumpTranscriptTailAsync(
        AcpManagedSession session,
        AcpDiscoveredSession discovered,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var ev in _runtime.TailSessionAsync(
                discovered,
                includeHistory: false,
                followLive: true,
                cancellationToken).ConfigureAwait(false))
            {
                await ApplyEventToSessionAsync(session, ev, isFromTranscriptTail: true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch
        {
            // Ignore transcript tail errors
        }
    }

    private async Task MonitorActiveSessionsLoopAsync(CancellationToken cancellationToken)
    {
        var tick = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(AcpGatewayDefaults.MonitorLoopInterval, cancellationToken).ConfigureAwait(false);
                tick++;

                if (tick % AcpGatewayDefaults.DiscoveryRefreshEveryMonitorTicks == 0)
                {
                    await RefreshDiscoveryAsync(cancellationToken).ConfigureAwait(false);
                }

                await CheckTranscriptFilesForActivityAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Ignore background monitor iteration errors
            }
        }
    }

    private async Task CheckTranscriptFilesForActivityAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (sessionKey, session) in _sessions)
        {
            var path = session.TranscriptPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var isRecentlyActive = string.Equals(sessionKey, _activeSessionKey, StringComparison.Ordinal) ||
                                   session.LiveHandle is not null ||
                                   (now - session.UpdatedAt) <= AcpGatewayDefaults.ActiveSessionRecencyThreshold;
            if (!isRecentlyActive)
            {
                continue;
            }

            FileInfo fi;
            try
            {
                fi = new FileInfo(path);
                if (!fi.Exists)
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            var lastWriteUtc = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero);
            var currentLen = fi.Length;
            AcpGatewaySessionSummary? changedSummary;

            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                changedSummary = session.AdvanceDiskActivityUnderGate(currentLen, lastWriteUtc);
            }
            finally
            {
                session.Gate.Release();
            }

            if (changedSummary is not null)
            {
                await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
                {
                    Type = RealtimeEnvelopeTypes.SessionSummary,
                    SessionKey = session.SessionKey,
                    Summary = changedSummary
                }).ConfigureAwait(false);
            }
        }
    }

    private void StartLiveHandlePump(AcpManagedSession session, IAcpSessionHandle handle)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        session.LivePumpCts = linkedCts;
        session.LivePumpTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var ev in handle.ReadEventsAsync(linkedCts.Token).ConfigureAwait(false))
                {
                    await ApplyEventToSessionAsync(session, ev, isFromTranscriptTail: false, linkedCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                await AppendSystemNoteAsync(session, $"Session stream ended: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
            }
        }, linkedCts.Token);
    }

    private async Task ApplyEventToSessionAsync(
        AcpManagedSession session,
        AcpEvent acpEvent,
        bool isFromTranscriptTail,
        CancellationToken cancellationToken)
    {
        SessionEventProjection projection;
        AcpGatewaySessionSummary summaryToBroadcast;
        DetachedLiveHandle detachedHandle = default;

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (!isFromTranscriptTail)
            {
                session.LastMobileTurnActivityAt = now;
                if (session.LiveHandle is not PtyAttachedSessionHandle)
                {
                    session.SuppressTranscriptTailUntilNextUserMessage = true;
                }
            }
            else
            {
                var decision = session.EvaluateTranscriptTailEventUnderGate(acpEvent, now);
                if (!decision.ShouldProcess)
                {
                    return;
                }
                detachedHandle = decision.DetachedHandle;
            }

            projection = AcpTimelineProjector.ApplyEventUnderGate(
                session,
                acpEvent,
                isHistoricalLoad: false,
                NextItemId);

            if (!isFromTranscriptTail)
            {
                detachedHandle = session.OnLiveEventAppliedUnderGate(acpEvent, now);
            }

            summaryToBroadcast = session.BuildSummaryUnderGate();
        }
        finally
        {
            session.Gate.Release();
        }

        detachedHandle.DisposeInBackground();
        await BroadcastProjectionAndSummaryAsync(session.SessionKey, projection, summaryToBroadcast).ConfigureAwait(false);
    }

    private async Task BroadcastProjectionAndSummaryAsync(
        string sessionKey,
        SessionEventProjection projection,
        AcpGatewaySessionSummary summary)
    {
        if (projection.PreceedingTimelineItem is not null)
        {
            await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
            {
                Type = RealtimeEnvelopeTypes.TimelineItem,
                SessionKey = sessionKey,
                TimelineItem = projection.PreceedingTimelineItem
            }).ConfigureAwait(false);
        }

        if (projection.TimelineItem is not null)
        {
            await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
            {
                Type = RealtimeEnvelopeTypes.TimelineItem,
                SessionKey = sessionKey,
                TimelineItem = projection.TimelineItem
            }).ConfigureAwait(false);
        }

        if (projection.QueuedPrompts is not null)
        {
            await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
            {
                Type = RealtimeEnvelopeTypes.QueueUpdated,
                SessionKey = sessionKey,
                QueuedPrompts = projection.QueuedPrompts
            }).ConfigureAwait(false);
        }

        if (projection.PendingPermissions is not null)
        {
            await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
            {
                Type = RealtimeEnvelopeTypes.PermissionUpdated,
                SessionKey = sessionKey,
                PendingPermissions = projection.PendingPermissions
            }).ConfigureAwait(false);
        }

        await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
        {
            Type = RealtimeEnvelopeTypes.SessionSummary,
            SessionKey = sessionKey,
            Summary = summary
        }).ConfigureAwait(false);
    }

    private async Task AppendUserPromptItemAsync(
        AcpManagedSession session,
        string prompt,
        string? badge,
        CancellationToken cancellationToken)
    {
        AcpGatewayTimelineItem item;
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            session.MarkMobileTurnStartingUnderGate(now, prompt);
            item = new AcpGatewayTimelineItem
            {
                Id = NextItemId(),
                Kind = TimelineItemKinds.Message,
                Role = AcpRoles.User,
                Timestamp = now,
                Text = prompt,
                Badge = badge
            };
            session.AddTimelineItemUnderGate(item);
        }
        finally
        {
            session.Gate.Release();
        }

        await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
        {
            Type = RealtimeEnvelopeTypes.TimelineItem,
            SessionKey = session.SessionKey,
            TimelineItem = item
        }).ConfigureAwait(false);
    }

    private async Task AppendSystemNoteAsync(
        AcpManagedSession session,
        string message,
        CancellationToken cancellationToken)
    {
        AcpGatewayTimelineItem item;
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            item = new AcpGatewayTimelineItem
            {
                Id = NextItemId(),
                Kind = TimelineItemKinds.SystemNote,
                Role = AcpRoles.System,
                Timestamp = DateTimeOffset.UtcNow,
                Text = message
            };
            session.AddTimelineItemUnderGate(item);
        }
        finally
        {
            session.Gate.Release();
        }

        await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
        {
            Type = RealtimeEnvelopeTypes.TimelineItem,
            SessionKey = session.SessionKey,
            TimelineItem = item
        }).ConfigureAwait(false);
    }

    private async Task BroadcastSessionSummaryAsync(AcpManagedSession session)
    {
        var summary = await session.BuildSummaryAsync(CancellationToken.None).ConfigureAwait(false);
        await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
        {
            Type = RealtimeEnvelopeTypes.SessionSummary,
            SessionKey = session.SessionKey,
            Summary = summary
        }).ConfigureAwait(false);
    }

    private async Task BroadcastStateAsync()
    {
        var state = await GetStateSnapshotAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
        await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
        {
            Type = RealtimeEnvelopeTypes.StateSnapshot,
            State = state
        }).ConfigureAwait(false);
    }

    private async Task BroadcastEnvelopeAsync(AcpGatewayRealtimeEnvelope envelope)
    {
        foreach (var (id, callback) in _subscribers)
        {
            try
            {
                await callback(envelope).ConfigureAwait(false);
            }
            catch
            {
                _subscribers.TryRemove(id, out _);
            }
        }
    }

    private string NextItemId() => $"item_{Interlocked.Increment(ref _timelineItemCounter)}";

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            {
                return;
            }

            await _lifetimeCts.CancelAsync().ConfigureAwait(false);
            _subscribers.Clear();
            await AcpManagedSession.WaitBackgroundTaskQuietlyAsync(_monitorTask).ConfigureAwait(false);

            var uniqueSessions = new HashSet<AcpManagedSession>(ReferenceEqualityComparer.Instance);
            foreach (var (_, session) in _sessions)
            {
                if (uniqueSessions.Add(session))
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }
            _sessions.Clear();

            if (_ownsRuntime)
            {
                await _runtime.DisposeAsync().ConfigureAwait(false);
            }

            _lifetimeCts.Dispose();
        }
        finally
        {
            _disposeGate.Release();
        }
    }

    private sealed class SubscriptionToken(
        ConcurrentDictionary<long, Func<AcpGatewayRealtimeEnvelope, Task>> dict,
        long id) : IDisposable
    {
        public void Dispose() => dict.TryRemove(id, out _);
    }
}
