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
    private const int DefaultTimelinePageSize = 30;
    private const int TimelinePageStep = 40;
    private const int MaxTimelineItemsPerSession = 2000;
    private const int MaxToolPayloadChars = 12000;

    private readonly AcpliteRuntime _runtime;
    private readonly bool _ownsRuntime;
    private readonly AgentProviderKind? _scopedProvider;
    private readonly string _defaultWorkingDirectory;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, ManagedSession> _sessions = new(StringComparer.Ordinal);
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
        _runtime = runtime ?? new AcpliteRuntime();
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
        await RefreshDiscoveryAsync(cancellationToken).ConfigureAwait(false);
        _monitorTask ??= Task.Run(() => MonitorActiveSessionsLoopAsync(_lifetimeCts.Token), _lifetimeCts.Token);
    }

    public async Task RefreshDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        var filteredProviders = await _runtime.DiscoverProvidersAsync(
            providerFilter: _scopedProvider,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _providers = filteredProviders;

        var discoveredSessions = await _runtime.DiscoverSessionsAsync(
            providerFilter: _scopedProvider,
            maxSessionsPerProvider: 30,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var disc in discoveredSessions)
            {
                var key = BuildSessionKey(disc.Provider, disc.SessionId);
                if (!_sessions.TryGetValue(key, out var existing))
                {
                    existing = new ManagedSession(disc.Provider, disc.SessionId)
                    {
                        Title = string.IsNullOrWhiteSpace(disc.Title) ? $"Session {ShortId(disc.SessionId)}" : disc.Title,
                        WorkingDirectory = disc.WorkingDirectory ?? _defaultWorkingDirectory,
                        TranscriptPath = disc.TranscriptPath,
                        GitBranch = disc.GitBranch,
                        Model = disc.Model,
                        CreatedAt = disc.CreatedAt,
                        UpdatedAt = disc.UpdatedAt,
                        DiscoveredMessageCount = disc.MessageCount,
                        IsDiscoveredOnDisk = true
                    };
                    _sessions[key] = existing;
                }
                else
                {
                    await existing.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(disc.Title) && existing.Title.StartsWith("Session ", StringComparison.Ordinal))
                        {
                            existing.Title = disc.Title;
                        }
                        existing.TranscriptPath ??= disc.TranscriptPath;
                        existing.WorkingDirectory ??= disc.WorkingDirectory;
                        existing.GitBranch ??= disc.GitBranch;
                        existing.Model ??= disc.Model;
                        if (disc.UpdatedAt > existing.UpdatedAt)
                        {
                            existing.UpdatedAt = disc.UpdatedAt;
                        }
                        existing.DiscoveredMessageCount = Math.Max(existing.DiscoveredMessageCount, disc.MessageCount);
                    }
                    finally
                    {
                        existing.Gate.Release();
                    }
                }
            }

            if (string.IsNullOrEmpty(_activeSessionKey) || !_sessions.ContainsKey(_activeSessionKey))
            {
                ManagedSession? mostRecent = null;
                foreach (var (_, candidate) in _sessions)
                {
                    if (mostRecent is null || candidate.UpdatedAt > mostRecent.UpdatedAt)
                    {
                        mostRecent = candidate;
                    }
                }
                _activeSessionKey = mostRecent?.SessionKey;
            }
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
            activeDetail = await BuildSessionDetailAsync(activeSession, cancellationToken).ConfigureAwait(false);
        }

        var summaries = new List<AcpGatewaySessionSummary>();
        var recentDirs = new HashSet<string>(StringComparer.Ordinal);
        recentDirs.Add(_defaultWorkingDirectory);

        foreach (var (_, session) in _sessions)
        {
            summaries.Add(await BuildSessionSummaryAsync(session, cancellationToken).ConfigureAwait(false));
            if (!string.IsNullOrWhiteSpace(session.WorkingDirectory))
            {
                recentDirs.Add(session.WorkingDirectory!);
            }
        }

        summaries.Sort(static (a, b) =>
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
        });

        var usages = new List<AcpUsageSnapshot>();
        foreach (var (_, usage) in _usageByProvider)
        {
            usages.Add(usage);
        }

        return new AcpGatewayStateSnapshot
        {
            MachineName = Environment.MachineName,
            DefaultWorkingDirectory = _defaultWorkingDirectory,
            ScopedProvider = _scopedProvider?.ToShortId(),
            Providers = _providers,
            UsageSnapshots = usages,
            Sessions = summaries,
            RecentDirectories = recentDirs.Take(8).ToArray(),
            ActiveSessionKey = targetKey,
            ActiveSession = activeDetail
        };
    }

    public async Task<AcpGatewayActionResponse> ExecuteActionAsync(
        AcpGatewayActionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await ExecuteActionCoreAsync(request, cancellationToken).ConfigureAwait(false);
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

    private async Task<AcpGatewayActionResponse> ExecuteActionCoreAsync(
        AcpGatewayActionRequest request,
        CancellationToken cancellationToken)
    {
        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();

        switch (action)
        {
            case "select_session":
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
                        : DefaultTimelinePageSize;
                }
                finally
                {
                    session.Gate.Release();
                }

                _activeSessionKey = session.SessionKey;
                await EnsureTailingStartedAsync(session, cancellationToken).ConfigureAwait(false);
                var detail = await BuildSessionDetailAsync(session, cancellationToken).ConfigureAwait(false);
                return new AcpGatewayActionResponse
                {
                    Ok = true,
                    SessionKey = session.SessionKey,
                    Session = detail
                };
            }

            case "load_older":
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
                        : session.VisibleTimelineLimit + TimelinePageStep;
                }
                finally
                {
                    session.Gate.Release();
                }

                var detail = await BuildSessionDetailAsync(session, cancellationToken).ConfigureAwait(false);
                return new AcpGatewayActionResponse
                {
                    Ok = true,
                    SessionKey = session.SessionKey,
                    Session = detail
                };
            }

            case "refresh_sessions":
            {
                await RefreshDiscoveryAsync(cancellationToken).ConfigureAwait(false);
                var state = await GetStateSnapshotAsync(request.SessionKey, cancellationToken).ConfigureAwait(false);
                await BroadcastStateAsync().ConfigureAwait(false);
                return new AcpGatewayActionResponse { Ok = true, State = state };
            }

            case "probe_usage":
            {
                await ProbeAllUsageAsync(cancellationToken).ConfigureAwait(false);
                var state = await GetStateSnapshotAsync(request.SessionKey, cancellationToken).ConfigureAwait(false);
                return new AcpGatewayActionResponse { Ok = true, State = state };
            }

            case "create_session":
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

            case "send_prompt":
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
                    Session = await BuildSessionDetailAsync(session, cancellationToken).ConfigureAwait(false)
                };
            }

            case "steer":
            {
                var session = await ResolveTargetSessionAsync(request.SessionKey, cancellationToken).ConfigureAwait(false);
                if (session is null || string.IsNullOrWhiteSpace(request.Prompt))
                {
                    return new AcpGatewayActionResponse { Ok = false, Message = "Select a session and enter steering instructions." };
                }

                var prompt = request.Prompt.Trim();
                var handle = await EnsureLiveHandleAttachedAsync(session, cancellationToken).ConfigureAwait(false);
                await AppendUserPromptItemAsync(session, prompt, badge: "STEERED", cancellationToken).ConfigureAwait(false);
                var accepted = await handle.SteerAsync(prompt, cancellationToken).ConfigureAwait(false);
                await BroadcastSessionSummaryAsync(session).ConfigureAwait(false);

                return new AcpGatewayActionResponse
                {
                    Ok = accepted,
                    Message = accepted ? "Live steering injected." : "Steering was not accepted by the provider.",
                    SessionKey = session.SessionKey,
                    Session = await BuildSessionDetailAsync(session, cancellationToken).ConfigureAwait(false)
                };
            }

            case "enqueue_prompt":
            {
                var session = await ResolveTargetSessionAsync(request.SessionKey, cancellationToken).ConfigureAwait(false);
                if (session is null || string.IsNullOrWhiteSpace(request.Prompt))
                {
                    return new AcpGatewayActionResponse { Ok = false, Message = "Select a session and enter a prompt to queue." };
                }

                var prompt = request.Prompt.Trim();
                var handle = await EnsureLiveHandleAttachedAsync(session, cancellationToken).ConfigureAwait(false);
                var wasBusy = handle.IsTurnActive;
                if (!wasBusy)
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
                    Session = await BuildSessionDetailAsync(session, cancellationToken).ConfigureAwait(false)
                };
            }

            case "remove_queued":
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
                    Session = await BuildSessionDetailAsync(session, cancellationToken).ConfigureAwait(false)
                };
            }

            case "clear_queued":
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
                    Session = session is not null ? await BuildSessionDetailAsync(session, cancellationToken).ConfigureAwait(false) : null
                };
            }

            case "interrupt_and_prompt":
            {
                var session = await ResolveTargetSessionAsync(request.SessionKey, cancellationToken).ConfigureAwait(false);
                if (session is null || string.IsNullOrWhiteSpace(request.Prompt))
                {
                    return new AcpGatewayActionResponse { Ok = false, Message = "Select a session and enter an interrupting prompt." };
                }

                var prompt = request.Prompt.Trim();
                var handle = await EnsureLiveHandleAttachedAsync(session, cancellationToken).ConfigureAwait(false);
                await AppendUserPromptItemAsync(session, prompt, badge: "INTERRUPT", cancellationToken).ConfigureAwait(false);
                await handle.InterruptAndSendPromptAsync(prompt, cancellationToken).ConfigureAwait(false);
                await BroadcastSessionSummaryAsync(session).ConfigureAwait(false);

                return new AcpGatewayActionResponse
                {
                    Ok = true,
                    Message = "Interrupted current turn and started new prompt.",
                    SessionKey = session.SessionKey,
                    Session = await BuildSessionDetailAsync(session, cancellationToken).ConfigureAwait(false)
                };
            }

            case "cancel_turn":
            {
                var session = await ResolveTargetSessionAsync(request.SessionKey, cancellationToken).ConfigureAwait(false);
                if (session?.LiveHandle is null)
                {
                    return new AcpGatewayActionResponse { Ok = false, Message = "No active live session to stop." };
                }

                await session.LiveHandle.CancelTurnAsync(cancellationToken).ConfigureAwait(false);
                await AppendSystemNoteAsync(session, "Turn interrupted from mobile.", cancellationToken).ConfigureAwait(false);
                await BroadcastSessionSummaryAsync(session).ConfigureAwait(false);

                return new AcpGatewayActionResponse
                {
                    Ok = true,
                    Message = "Interrupt signal sent.",
                    SessionKey = session.SessionKey,
                    Session = await BuildSessionDetailAsync(session, cancellationToken).ConfigureAwait(false)
                };
            }

            case "respond_permission":
            {
                var session = await ResolveTargetSessionAsync(request.SessionKey, cancellationToken).ConfigureAwait(false);
                if (session?.LiveHandle is null || string.IsNullOrWhiteSpace(request.PermissionRequestId))
                {
                    return new AcpGatewayActionResponse { Ok = false, Message = "Permission request is no longer active." };
                }

                var approved = request.Approved ?? true;
                await session.LiveHandle.RespondPermissionAsync(
                    request.PermissionRequestId,
                    approved,
                    request.SelectedOptionId,
                    cancellationToken).ConfigureAwait(false);

                await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                IReadOnlyList<AcpGatewayPendingPermission> remainingPermissions;
                try
                {
                    session.PendingPermissions.RemoveAll(p => string.Equals(p.RequestId, request.PermissionRequestId, StringComparison.Ordinal));
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
                    Type = "permission_updated",
                    SessionKey = session.SessionKey,
                    PendingPermissions = remainingPermissions
                }).ConfigureAwait(false);
                await BroadcastSessionSummaryAsync(session).ConfigureAwait(false);

                return new AcpGatewayActionResponse
                {
                    Ok = true,
                    SessionKey = session.SessionKey,
                    Session = await BuildSessionDetailAsync(session, cancellationToken).ConfigureAwait(false)
                };
            }

            case "set_model":
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
                    Session = await BuildSessionDetailAsync(session, cancellationToken).ConfigureAwait(false)
                };
            }

            default:
                return new AcpGatewayActionResponse { Ok = false, Message = $"Unknown action '{request.Action}'." };
        }
    }

    private async Task<ManagedSession?> ResolveTargetSessionAsync(string? sessionKey, CancellationToken cancellationToken)
    {
        var key = !string.IsNullOrWhiteSpace(sessionKey) ? sessionKey : _activeSessionKey;
        if (!string.IsNullOrWhiteSpace(key) && _sessions.TryGetValue(key, out var found))
        {
            return found;
        }

        await RefreshDiscoveryAsync(cancellationToken).ConfigureAwait(false);
        key = !string.IsNullOrWhiteSpace(sessionKey) ? sessionKey : _activeSessionKey;
        return !string.IsNullOrWhiteSpace(key) && _sessions.TryGetValue(key, out var afterRefresh) ? afterRefresh : null;
    }

    private async Task<ManagedSession> CreateInteractiveSessionAsync(
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

        var session = new ManagedSession(provider, handle.SessionId)
        {
            Title = !string.IsNullOrWhiteSpace(initialPrompt)
                ? TruncateTitle(initialPrompt!)
                : $"New {provider.ToDisplayName()} Session",
            WorkingDirectory = workingDirectory,
            Model = model,
            AutoApproveTools = autoApproveTools,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LiveHandle = handle
        };

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

    private async Task<IAcpSessionHandle> EnsureLiveHandleAttachedAsync(
        ManagedSession session,
        CancellationToken cancellationToken)
    {
        await session.AttachGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancellationTokenSource? oldTailCts;
            Task? oldTailTask;
            AcpSessionConfig config;

            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (session.LiveHandle is not null)
                {
                    return session.LiveHandle;
                }

                oldTailCts = session.TailCts;
                oldTailTask = session.TailTask;
                session.TailCts = null;
                session.TailTask = null;

                config = new AcpSessionConfig
                {
                    Provider = session.Provider,
                    WorkingDirectory = session.WorkingDirectory ?? _defaultWorkingDirectory,
                    SessionId = session.SessionId,
                    ResumeExisting = session.IsDiscoveredOnDisk,
                    Model = session.Model,
                    AutoApproveTools = session.AutoApproveTools
                };
            }
            finally
            {
                session.Gate.Release();
            }

            if (oldTailCts is not null)
            {
                await oldTailCts.CancelAsync().ConfigureAwait(false);
                await WaitBackgroundTaskQuietlyAsync(oldTailTask).ConfigureAwait(false);
                oldTailCts.Dispose();
            }

            var handle = await _runtime.StartSessionAsync(config, cancellationToken).ConfigureAwait(false);

            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                session.LiveHandle = handle;
                session.UpdatedAt = DateTimeOffset.UtcNow;
                StartLiveHandlePump(session, handle);
            }
            finally
            {
                session.Gate.Release();
            }

            var actualKey = BuildSessionKey(session.Provider, handle.SessionId);
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

    private async Task EnsureTailingStartedAsync(ManagedSession session, CancellationToken cancellationToken)
    {
        await session.AttachGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AcpDiscoveredSession? discoveredForHistory = null;
            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (session.LiveHandle is not null ||
                    string.IsNullOrWhiteSpace(session.TranscriptPath) ||
                    !File.Exists(session.TranscriptPath))
                {
                    return;
                }

                if (!session.InitialHistoryLoaded)
                {
                    discoveredForHistory = new AcpDiscoveredSession(
                        Provider: session.Provider,
                        SessionId: session.SessionId,
                        Title: session.Title,
                        WorkingDirectory: session.WorkingDirectory,
                        TranscriptPath: session.TranscriptPath!,
                        UpdatedAt: session.UpdatedAt,
                        CreatedAt: session.CreatedAt,
                        Model: session.Model,
                        GitBranch: session.GitBranch);
                }
            }
            finally
            {
                session.Gate.Release();
            }

            if (discoveredForHistory is not null)
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
                    if (!session.InitialHistoryLoaded && session.LiveHandle is null)
                    {
                        foreach (var ev in historicalEvents)
                        {
                            ApplyEventUnderGate(session, ev, isHistoricalLoad: true, out _, out _, out _, out _);
                        }
                        session.InitialHistoryLoaded = true;
                        try
                        {
                            var fi = new FileInfo(discoveredForHistory.TranscriptPath);
                            if (fi.Exists)
                            {
                                session.LastObservedFileLength = fi.Length;
                                var lastWrite = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero);
                                if (lastWrite > session.UpdatedAt)
                                {
                                    session.UpdatedAt = lastWrite;
                                }
                            }
                        }
                        catch
                        {
                            // Ignore file stat error
                        }
                    }
                }
                finally
                {
                    session.Gate.Release();
                }
            }

            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (session.LiveHandle is not null ||
                    session.TailTask is not null ||
                    string.IsNullOrWhiteSpace(session.TranscriptPath))
                {
                    return;
                }

                var tailCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                session.TailCts = tailCts;
                var discovered = new AcpDiscoveredSession(
                    Provider: session.Provider,
                    SessionId: session.SessionId,
                    Title: session.Title,
                    WorkingDirectory: session.WorkingDirectory,
                    TranscriptPath: session.TranscriptPath!,
                    UpdatedAt: session.UpdatedAt,
                    CreatedAt: session.CreatedAt,
                    Model: session.Model,
                    GitBranch: session.GitBranch);

                session.TailTask = Task.Run(() => PumpTranscriptTailAsync(session, discovered, tailCts.Token), tailCts.Token);
            }
            finally
            {
                session.Gate.Release();
            }
        }
        finally
        {
            session.AttachGate.Release();
        }
    }

    private async Task PumpTranscriptTailAsync(
        ManagedSession session,
        AcpDiscoveredSession discovered,
        CancellationToken cancellationToken)
    {
        try
        {
            var adapter = _runtime.GetAdapter(session.Provider);
            await foreach (var ev in adapter.TailSessionTranscriptAsync(
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
            // Normal stop (either switched to live handle or shutting down)
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
                await Task.Delay(TimeSpan.FromSeconds(2.5), cancellationToken).ConfigureAwait(false);
                tick++;

                if (tick % 5 == 0)
                {
                    await RefreshDiscoveryAsync(cancellationToken).ConfigureAwait(false);
                }

                foreach (var (_, session) in _sessions)
                {
                    var path = session.TranscriptPath;
                    if (string.IsNullOrWhiteSpace(path))
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
                    AcpGatewaySessionSummary? changedSummary = null;

                    await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var lengthGrew = session.LastObservedFileLength > 0 && currentLen > session.LastObservedFileLength;
                        var writeAdvanced = lastWriteUtc > session.UpdatedAt.AddSeconds(1);

                        if (session.LastObservedFileLength == 0)
                        {
                            session.LastObservedFileLength = currentLen;
                        }

                        if (lengthGrew || writeAdvanced)
                        {
                            session.LastObservedFileLength = currentLen;
                            session.UpdatedAt = lastWriteUtc > DateTimeOffset.UtcNow ? DateTimeOffset.UtcNow : lastWriteUtc;
                            if (lengthGrew)
                            {
                                session.LastDiskActivityAt = DateTimeOffset.UtcNow;
                            }
                            changedSummary = BuildSessionSummaryUnderGate(session);
                        }
                    }
                    finally
                    {
                        session.Gate.Release();
                    }

                    if (changedSummary is not null)
                    {
                        await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
                        {
                            Type = "session_summary",
                            SessionKey = session.SessionKey,
                            Summary = changedSummary
                        }).ConfigureAwait(false);
                    }
                }
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

    private void StartLiveHandlePump(ManagedSession session, IAcpSessionHandle handle)
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
        ManagedSession session,
        AcpEvent acpEvent,
        bool isFromTranscriptTail,
        CancellationToken cancellationToken)
    {
        AcpGatewayTimelineItem? itemToBroadcast = null;
        IReadOnlyList<AcpQueuedPromptItem>? queueToBroadcast = null;
        IReadOnlyList<AcpGatewayPendingPermission>? permissionsToBroadcast = null;
        AcpGatewayTimelineItem? autoDrainedUserItem = null;
        AcpGatewaySessionSummary? summaryToBroadcast = null;

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (isFromTranscriptTail && session.LiveHandle is not null)
            {
                return;
            }

            if (isFromTranscriptTail)
            {
                session.LastDiskActivityAt = DateTimeOffset.UtcNow;
            }

            ApplyEventUnderGate(
                session,
                acpEvent,
                isHistoricalLoad: false,
                out itemToBroadcast,
                out queueToBroadcast,
                out permissionsToBroadcast,
                out autoDrainedUserItem);

            summaryToBroadcast = BuildSessionSummaryUnderGate(session);
        }
        finally
        {
            session.Gate.Release();
        }

        if (autoDrainedUserItem is not null)
        {
            await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
            {
                Type = "timeline_item",
                SessionKey = session.SessionKey,
                TimelineItem = autoDrainedUserItem
            }).ConfigureAwait(false);
        }

        if (itemToBroadcast is not null)
        {
            await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
            {
                Type = "timeline_item",
                SessionKey = session.SessionKey,
                TimelineItem = itemToBroadcast
            }).ConfigureAwait(false);
        }

        if (queueToBroadcast is not null)
        {
            await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
            {
                Type = "queue_updated",
                SessionKey = session.SessionKey,
                QueuedPrompts = queueToBroadcast
            }).ConfigureAwait(false);
        }

        if (permissionsToBroadcast is not null)
        {
            await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
            {
                Type = "permission_updated",
                SessionKey = session.SessionKey,
                PendingPermissions = permissionsToBroadcast
            }).ConfigureAwait(false);
        }

        if (summaryToBroadcast is not null)
        {
            await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
            {
                Type = "session_summary",
                SessionKey = session.SessionKey,
                Summary = summaryToBroadcast
            }).ConfigureAwait(false);
        }
    }

    private async Task AppendUserPromptItemAsync(
        ManagedSession session,
        string prompt,
        string? badge,
        CancellationToken cancellationToken)
    {
        AcpGatewayTimelineItem item;
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session.UpdatedAt = DateTimeOffset.UtcNow;
            if (session.Title.StartsWith("New ", StringComparison.Ordinal) ||
                session.Title.StartsWith("Session ", StringComparison.Ordinal))
            {
                session.Title = TruncateTitle(prompt);
            }

            item = new AcpGatewayTimelineItem
            {
                Id = NextItemId(),
                Kind = "message",
                Role = "user",
                Timestamp = session.UpdatedAt,
                Text = prompt,
                Badge = badge
            };
            AddTimelineItemUnderGate(session, item);
        }
        finally
        {
            session.Gate.Release();
        }

        await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
        {
            Type = "timeline_item",
            SessionKey = session.SessionKey,
            TimelineItem = item
        }).ConfigureAwait(false);
    }

    private async Task AppendSystemNoteAsync(
        ManagedSession session,
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
                Kind = "system_note",
                Role = "system",
                Timestamp = DateTimeOffset.UtcNow,
                Text = message
            };
            AddTimelineItemUnderGate(session, item);
        }
        finally
        {
            session.Gate.Release();
        }

        await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
        {
            Type = "timeline_item",
            SessionKey = session.SessionKey,
            TimelineItem = item
        }).ConfigureAwait(false);
    }

    private void ApplyEventUnderGate(
        ManagedSession session,
        AcpEvent acpEvent,
        bool isHistoricalLoad,
        out AcpGatewayTimelineItem? itemToBroadcast,
        out IReadOnlyList<AcpQueuedPromptItem>? queueToBroadcast,
        out IReadOnlyList<AcpGatewayPendingPermission>? permissionsToBroadcast,
        out AcpGatewayTimelineItem? autoDrainedUserItem)
    {
        itemToBroadcast = null;
        queueToBroadcast = null;
        permissionsToBroadcast = null;
        autoDrainedUserItem = null;

        if (!isHistoricalLoad || acpEvent.Timestamp > session.UpdatedAt)
        {
            session.UpdatedAt = acpEvent.Timestamp;
        }

        switch (acpEvent)
        {
            case AcpSessionStartedEvent started:
            {
                if (!string.IsNullOrWhiteSpace(started.Model))
                {
                    session.Model = started.Model;
                }
                if (!string.IsNullOrWhiteSpace(started.WorkingDirectory))
                {
                    session.WorkingDirectory = started.WorkingDirectory;
                }
                break;
            }

            case AcpTranscriptHistoryEntryEvent hist:
            {
                if (string.IsNullOrWhiteSpace(hist.Text) &&
                    string.IsNullOrWhiteSpace(hist.Thinking) &&
                    string.IsNullOrWhiteSpace(hist.ToolName))
                {
                    break;
                }

                var isToolOnly = !string.IsNullOrWhiteSpace(hist.ToolName) &&
                                 string.IsNullOrWhiteSpace(hist.Thinking) &&
                                 (hist.Role is "tool" or "system" ||
                                  string.IsNullOrWhiteSpace(hist.Text) ||
                                  string.Equals(hist.Text, hist.ToolName, StringComparison.Ordinal));

                if (isToolOnly)
                {
                    var toolItem = new AcpGatewayTimelineItem
                    {
                        Id = NextItemId(),
                        Kind = "tool_call",
                        Role = "assistant",
                        Timestamp = hist.Timestamp,
                        Text = string.IsNullOrWhiteSpace(hist.Text) ? hist.ToolName : TruncateLargePayload(hist.Text, 240),
                        ToolName = hist.ToolName,
                        ToolCallId = hist.ToolCallId,
                        ToolStatus = "completed",
                        RawOutput = !string.IsNullOrWhiteSpace(hist.Text) && !string.Equals(hist.Text, hist.ToolName, StringComparison.Ordinal)
                            ? TruncateLargePayload(hist.Text, MaxToolPayloadChars)
                            : null
                    };
                    AddTimelineItemUnderGate(session, toolItem);
                    itemToBroadcast = toolItem;
                    break;
                }

                // Coalesce consecutive assistant thinking + text steps
                var prev = session.Timeline.Count > 0 ? session.Timeline[^1] : null;
                if (hist.Role == "assistant" &&
                    prev is not null &&
                    prev.Role == "assistant" &&
                    prev.Kind == "message" &&
                    string.IsNullOrWhiteSpace(prev.Text) &&
                    !string.IsNullOrWhiteSpace(prev.Thinking) &&
                    string.IsNullOrWhiteSpace(hist.Thinking) &&
                    !string.IsNullOrWhiteSpace(hist.Text))
                {
                    var merged = prev with
                    {
                        Text = hist.Text,
                        Timestamp = hist.Timestamp,
                        ToolName = hist.ToolName ?? prev.ToolName,
                        ToolCallId = hist.ToolCallId ?? prev.ToolCallId
                    };
                    session.Timeline[^1] = merged;
                    itemToBroadcast = merged;
                    break;
                }

                var item = new AcpGatewayTimelineItem
                {
                    Id = NextItemId(),
                    Kind = "message",
                    Role = hist.Role,
                    Timestamp = hist.Timestamp,
                    Text = hist.Text,
                    Thinking = TruncateLargePayload(hist.Thinking, MaxToolPayloadChars),
                    ToolName = hist.ToolName,
                    ToolCallId = hist.ToolCallId,
                    Badge = !string.IsNullOrWhiteSpace(hist.ToolName) ? $"TOOL: {hist.ToolName}" : null
                };
                AddTimelineItemUnderGate(session, item);
                itemToBroadcast = item;
                break;
            }

            case AcpThinkingDeltaEvent thinkingDelta:
            {
                var last = session.Timeline.Count > 0 ? session.Timeline[^1] : null;
                if (last is not null && last.Role == "assistant" && last.Kind == "message")
                {
                    var updated = last with
                    {
                        Thinking = TruncateLargePayload((last.Thinking ?? string.Empty) + thinkingDelta.Delta, MaxToolPayloadChars),
                        Timestamp = thinkingDelta.Timestamp
                    };
                    session.Timeline[^1] = updated;
                    itemToBroadcast = updated;
                }
                else
                {
                    var created = new AcpGatewayTimelineItem
                    {
                        Id = NextItemId(),
                        Kind = "message",
                        Role = "assistant",
                        Timestamp = thinkingDelta.Timestamp,
                        Thinking = TruncateLargePayload(thinkingDelta.Delta, MaxToolPayloadChars),
                        Text = string.Empty
                    };
                    AddTimelineItemUnderGate(session, created);
                    itemToBroadcast = created;
                }
                break;
            }

            case AcpAssistantTextDeltaEvent textDelta:
            {
                var last = session.Timeline.Count > 0 ? session.Timeline[^1] : null;
                if (last is not null && last.Role == "assistant" && last.Kind == "message")
                {
                    var updated = last with
                    {
                        Text = (last.Text ?? string.Empty) + textDelta.Delta,
                        Timestamp = textDelta.Timestamp
                    };
                    session.Timeline[^1] = updated;
                    itemToBroadcast = updated;
                }
                else
                {
                    var created = new AcpGatewayTimelineItem
                    {
                        Id = NextItemId(),
                        Kind = "message",
                        Role = "assistant",
                        Timestamp = textDelta.Timestamp,
                        Text = textDelta.Delta
                    };
                    AddTimelineItemUnderGate(session, created);
                    itemToBroadcast = created;
                }
                break;
            }

            case AcpToolCallEvent toolCall:
            {
                var existingIdx = -1;
                for (var i = session.Timeline.Count - 1; i >= 0; i--)
                {
                    if (session.Timeline[i].Kind == "tool_call" &&
                        string.Equals(session.Timeline[i].ToolCallId, toolCall.ToolCallId, StringComparison.Ordinal))
                    {
                        existingIdx = i;
                        break;
                    }
                }

                if (existingIdx >= 0)
                {
                    var updated = session.Timeline[existingIdx] with
                    {
                        ToolStatus = toolCall.Status,
                        RawOutput = TruncateLargePayload(toolCall.RawOutput, MaxToolPayloadChars) ?? session.Timeline[existingIdx].RawOutput,
                        Timestamp = toolCall.Timestamp
                    };
                    session.Timeline[existingIdx] = updated;
                    itemToBroadcast = updated;
                }
                else
                {
                    var created = new AcpGatewayTimelineItem
                    {
                        Id = NextItemId(),
                        Kind = "tool_call",
                        Role = "assistant",
                        Timestamp = toolCall.Timestamp,
                        Text = toolCall.Title,
                        ToolCallId = toolCall.ToolCallId,
                        ToolName = toolCall.ToolName,
                        ToolKind = toolCall.Kind,
                        ToolStatus = toolCall.Status,
                        RawInputJson = TruncateLargePayload(toolCall.RawInputJson, MaxToolPayloadChars),
                        RawOutput = TruncateLargePayload(toolCall.RawOutput, MaxToolPayloadChars)
                    };
                    AddTimelineItemUnderGate(session, created);
                    itemToBroadcast = created;
                }
                break;
            }

            case AcpPermissionRequestEvent perm:
            {
                var pending = new AcpGatewayPendingPermission
                {
                    RequestId = perm.RequestId,
                    SessionKey = session.SessionKey,
                    Provider = session.Provider.ToShortId(),
                    ToolName = perm.ToolName,
                    Title = perm.Title,
                    Description = perm.Description,
                    RawInputJson = TruncateLargePayload(perm.RawInputJson, MaxToolPayloadChars),
                    Options = perm.Options,
                    RequestedAt = perm.Timestamp
                };
                session.PendingPermissions.RemoveAll(p => string.Equals(p.RequestId, perm.RequestId, StringComparison.Ordinal));
                session.PendingPermissions.Add(pending);
                permissionsToBroadcast = session.PendingPermissions.ToArray();
                break;
            }

            case AcpPromptQueueUpdatedEvent queueUpdated:
            {
                if (session.LastKnownQueue.Count > queueUpdated.QueuedPrompts.Count &&
                    session.LastKnownQueue.Count > 0)
                {
                    var previousHead = session.LastKnownQueue[0];
                    var stillPresent = queueUpdated.QueuedPrompts.Any(q => string.Equals(q.QueueItemId, previousHead.QueueItemId, StringComparison.Ordinal));
                    if (!stillPresent && session.LastTurnJustCompleted)
                    {
                        autoDrainedUserItem = new AcpGatewayTimelineItem
                        {
                            Id = NextItemId(),
                            Kind = "message",
                            Role = "user",
                            Timestamp = queueUpdated.Timestamp,
                            Text = previousHead.Prompt,
                            Badge = "QUEUED"
                        };
                        AddTimelineItemUnderGate(session, autoDrainedUserItem);
                    }
                }

                session.LastTurnJustCompleted = false;
                session.LastKnownQueue = queueUpdated.QueuedPrompts;
                queueToBroadcast = queueUpdated.QueuedPrompts;
                break;
            }

            case AcpInterruptionEvent interruption:
            {
                var item = new AcpGatewayTimelineItem
                {
                    Id = NextItemId(),
                    Kind = "interruption",
                    Role = "system",
                    Timestamp = interruption.Timestamp,
                    Text = interruption.Classification.Message,
                    Badge = interruption.Classification.Kind.ToString(),
                    IsError = true,
                    IsRetryable = interruption.Classification.IsRetryable
                };
                AddTimelineItemUnderGate(session, item);
                itemToBroadcast = item;
                break;
            }

            case AcpTurnCompletedEvent turnCompleted:
            {
                session.LastTurnJustCompleted = true;
                var item = new AcpGatewayTimelineItem
                {
                    Id = NextItemId(),
                    Kind = "turn_completed",
                    Role = "system",
                    Timestamp = turnCompleted.Timestamp,
                    StopReason = turnCompleted.StopReason,
                    InputTokens = turnCompleted.InputTokens,
                    OutputTokens = turnCompleted.OutputTokens,
                    CostUsd = turnCompleted.CostUsd,
                    DurationMs = turnCompleted.DurationMs,
                    IsError = string.Equals(turnCompleted.StopReason, "error", StringComparison.OrdinalIgnoreCase)
                };
                AddTimelineItemUnderGate(session, item);
                itemToBroadcast = item;
                break;
            }
        }
    }

    private static string? TruncateLargePayload(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        {
            return value;
        }
        return value[..maxChars] + "\n... [truncated]";
    }

    private static void AddTimelineItemUnderGate(ManagedSession session, AcpGatewayTimelineItem item)
    {
        session.Timeline.Add(item);
        while (session.Timeline.Count > MaxTimelineItemsPerSession)
        {
            session.Timeline.RemoveAt(0);
        }
    }

    private async Task<AcpGatewaySessionSummary> BuildSessionSummaryAsync(
        ManagedSession session,
        CancellationToken cancellationToken)
    {
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return BuildSessionSummaryUnderGate(session);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private static AcpGatewaySessionSummary BuildSessionSummaryUnderGate(ManagedSession session)
    {
        var handle = session.LiveHandle;
        var queuedCount = handle?.GetQueuedPrompts().Count ?? 0;
        var now = DateTimeOffset.UtcNow;
        var recentDiskTurn = (now - session.LastDiskActivityAt) <= TimeSpan.FromSeconds(35);
        var isTurnActive = (handle?.IsTurnActive ?? false) || recentDiskTurn;
        var isActiveNow = isTurnActive || handle is not null || (now - session.UpdatedAt) <= TimeSpan.FromMinutes(10);

        return new AcpGatewaySessionSummary
        {
            SessionKey = session.SessionKey,
            Provider = session.Provider.ToShortId(),
            ProviderDisplayName = session.Provider.ToDisplayName(),
            SessionId = session.SessionId,
            Title = session.Title,
            WorkingDirectory = session.WorkingDirectory,
            GitBranch = session.GitBranch,
            Model = session.Model,
            UpdatedAt = session.UpdatedAt,
            CreatedAt = session.CreatedAt,
            IsLiveAttached = handle is not null,
            IsTurnActive = isTurnActive,
            IsActiveNow = isActiveNow,
            HasPendingPermission = session.PendingPermissions.Count > 0,
            QueuedPromptCount = queuedCount,
            MessageCount = Math.Max(session.DiscoveredMessageCount, session.Timeline.Count)
        };
    }

    private async Task<AcpGatewaySessionDetail> BuildSessionDetailAsync(
        ManagedSession session,
        CancellationToken cancellationToken)
    {
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var summary = BuildSessionSummaryUnderGate(session);
            var queued = session.LiveHandle?.GetQueuedPrompts() ?? [];
            var totalCount = session.Timeline.Count;
            var limit = Math.Max(DefaultTimelinePageSize, session.VisibleTimelineLimit);
            var skip = Math.Max(0, totalCount - limit);
            var slice = skip == 0
                ? session.Timeline.ToArray()
                : session.Timeline.GetRange(skip, totalCount - skip).ToArray();

            return new AcpGatewaySessionDetail
            {
                Summary = summary,
                Timeline = slice,
                TotalTimelineCount = totalCount,
                HiddenOlderCount = skip,
                HasOlderMessages = skip > 0,
                QueuedPrompts = queued,
                PendingPermissions = session.PendingPermissions.ToArray()
            };
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task BroadcastSessionSummaryAsync(ManagedSession session)
    {
        var summary = await BuildSessionSummaryAsync(session, CancellationToken.None).ConfigureAwait(false);
        await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
        {
            Type = "session_summary",
            SessionKey = session.SessionKey,
            Summary = summary
        }).ConfigureAwait(false);
    }

    private async Task BroadcastStateAsync()
    {
        var state = await GetStateSnapshotAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
        await BroadcastEnvelopeAsync(new AcpGatewayRealtimeEnvelope
        {
            Type = "state_snapshot",
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

    private static string BuildSessionKey(AgentProviderKind provider, string sessionId)
        => $"{provider.ToShortId()}:{sessionId}";

    private static string ShortId(string id)
        => id.Length <= 8 ? id : id[..8];

    private static string TruncateTitle(string text)
    {
        var firstLine = text.Split('\n', '\r')[0].Trim();
        return firstLine.Length <= 72 ? firstLine : firstLine[..69] + "...";
    }

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
            await WaitBackgroundTaskQuietlyAsync(_monitorTask).ConfigureAwait(false);

            var uniqueSessions = new HashSet<ManagedSession>(ReferenceEqualityComparer.Instance);
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

    private static async Task WaitBackgroundTaskQuietlyAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
        catch
        {
            // Ignore cancellation or completion errors on teardown
        }
    }

    private sealed class SubscriptionToken(ConcurrentDictionary<long, Func<AcpGatewayRealtimeEnvelope, Task>> dict, long id) : IDisposable
    {
        public void Dispose() => dict.TryRemove(id, out _);
    }

    private sealed class ManagedSession(AgentProviderKind provider, string sessionId) : IAsyncDisposable
    {
        public AgentProviderKind Provider { get; } = provider;
        public string SessionId { get; } = sessionId;
        public string SessionKey { get; } = BuildSessionKey(provider, sessionId);
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public SemaphoreSlim AttachGate { get; } = new(1, 1);
        public List<AcpGatewayTimelineItem> Timeline { get; } = [];
        public List<AcpGatewayPendingPermission> PendingPermissions { get; } = [];
        public IReadOnlyList<AcpQueuedPromptItem> LastKnownQueue { get; set; } = [];
        public bool LastTurnJustCompleted { get; set; }

        public string Title { get; set; } = $"Session {ShortId(sessionId)}";
        public string? WorkingDirectory { get; set; }
        public string? TranscriptPath { get; set; }
        public string? GitBranch { get; set; }
        public string? Model { get; set; }
        public bool AutoApproveTools { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastDiskActivityAt { get; set; } = DateTimeOffset.MinValue;
        public long LastObservedFileLength { get; set; }
        public int DiscoveredMessageCount { get; set; }
        public bool IsDiscoveredOnDisk { get; set; }
        public bool InitialHistoryLoaded { get; set; }
        public int VisibleTimelineLimit { get; set; } = DefaultTimelinePageSize;

        public IAcpSessionHandle? LiveHandle { get; set; }
        public CancellationTokenSource? TailCts { get; set; }
        public Task? TailTask { get; set; }
        public CancellationTokenSource? LivePumpCts { get; set; }
        public Task? LivePumpTask { get; set; }

        public async ValueTask DisposeAsync()
        {
            if (TailCts is not null)
            {
                await TailCts.CancelAsync().ConfigureAwait(false);
            }
            if (LivePumpCts is not null)
            {
                await LivePumpCts.CancelAsync().ConfigureAwait(false);
            }
            if (LiveHandle is not null)
            {
                await LiveHandle.DisposeAsync().ConfigureAwait(false);
            }
            await WaitBackgroundTaskQuietlyAsync(TailTask).ConfigureAwait(false);
            await WaitBackgroundTaskQuietlyAsync(LivePumpTask).ConfigureAwait(false);
            TailCts?.Dispose();
            LivePumpCts?.Dispose();
            Gate.Dispose();
            AttachGate.Dispose();
        }
    }
}
