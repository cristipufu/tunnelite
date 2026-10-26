using Acplite.Sdk.Protocol;
using Acplite.Sdk.Runtime;

namespace Tunnelite.Acp;

internal readonly record struct DetachedLiveHandle(
    IAcpSessionHandle? Handle,
    CancellationTokenSource? PumpCts,
    Task? PumpTask)
{
    public bool HasResources => Handle is not null || PumpCts is not null;

    public async ValueTask DisposeAsync()
    {
        if (PumpCts is not null)
        {
            try
            {
                await PumpCts.CancelAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore cancellation race
            }

            await AcpManagedSession.WaitBackgroundTaskQuietlyAsync(PumpTask).ConfigureAwait(false);
            PumpCts.Dispose();
        }

        if (Handle is not null)
        {
            try
            {
                await Handle.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore handle teardown error
            }
        }
    }

    public void DisposeInBackground()
    {
        if (!HasResources)
        {
            return;
        }

        var copy = this;
        _ = Task.Run(async () => await copy.DisposeAsync().ConfigureAwait(false), CancellationToken.None);
    }
}

internal readonly record struct TranscriptTailDecision(
    bool ShouldProcess,
    DetachedLiveHandle DetachedHandle = default);

internal sealed class AcpManagedSession(AgentProviderKind provider, string sessionId) : IAsyncDisposable
{
    public AgentProviderKind Provider { get; } = provider;
    public string SessionId { get; } = sessionId;
    public string SessionKey { get; } = AcpTextFormatter.BuildSessionKey(provider, sessionId);
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public SemaphoreSlim AttachGate { get; } = new(1, 1);
    public List<AcpGatewayTimelineItem> Timeline { get; } = [];
    public List<AcpGatewayPendingPermission> PendingPermissions { get; } = [];
    public IReadOnlyList<AcpQueuedPromptItem> LastKnownQueue { get; set; } = [];
    public bool LastTurnJustCompleted { get; set; }

    public string Title { get; set; } = AcpTextFormatter.CreateDefaultSessionTitle(sessionId);
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
    public bool HasMobileAttachedOnce { get; set; }
    public bool HasExternalTranscriptActivitySinceAttach { get; set; }
    public bool SuppressTranscriptTailUntilNextUserMessage { get; set; }
    public DateTimeOffset MobileTurnStartingUntil { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastMobileTurnActivityAt { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastMobileTurnCompletedAt { get; set; } = DateTimeOffset.MinValue;
    public string? LastMobilePromptText { get; set; }
    public string? LastMobileAssistantText { get; set; }
    public int VisibleTimelineLimit { get; set; } = AcpGatewayDefaults.DefaultTimelinePageSize;
    public string? CurrentStreamingThoughtBuffer { get; set; }

    public IAcpSessionHandle? LiveHandle { get; set; }
    public CancellationTokenSource? TailCts { get; set; }
    public Task? TailTask { get; set; }
    public CancellationTokenSource? LivePumpCts { get; set; }
    public Task? LivePumpTask { get; set; }

    public static AcpManagedSession FromDiscovered(AcpDiscoveredSession discovered, string defaultWorkingDirectory)
    {
        return new AcpManagedSession(discovered.Provider, discovered.SessionId)
        {
            Title = string.IsNullOrWhiteSpace(discovered.Title)
                ? AcpTextFormatter.CreateDefaultSessionTitle(discovered.SessionId)
                : discovered.Title,
            WorkingDirectory = discovered.WorkingDirectory ?? defaultWorkingDirectory,
            TranscriptPath = discovered.TranscriptPath,
            GitBranch = discovered.GitBranch,
            Model = discovered.Model,
            CreatedAt = discovered.CreatedAt,
            UpdatedAt = discovered.UpdatedAt,
            DiscoveredMessageCount = discovered.MessageCount,
            IsDiscoveredOnDisk = true
        };
    }

    public static AcpManagedSession FromInteractive(
        AgentProviderKind provider,
        IAcpSessionHandle handle,
        string workingDirectory,
        string? model,
        bool autoApproveTools,
        string? initialPrompt)
    {
        var now = DateTimeOffset.UtcNow;
        return new AcpManagedSession(provider, handle.SessionId)
        {
            Title = !string.IsNullOrWhiteSpace(initialPrompt)
                ? AcpTextFormatter.TruncateTitle(initialPrompt)
                : AcpTextFormatter.CreateNewSessionTitle(provider),
            WorkingDirectory = workingDirectory,
            Model = model,
            AutoApproveTools = autoApproveTools,
            CreatedAt = now,
            UpdatedAt = now,
            LiveHandle = handle
        };
    }

    public void UpdateFromDiscoveryUnderGate(AcpDiscoveredSession discovered)
    {
        if (!string.IsNullOrWhiteSpace(discovered.Title) &&
            AcpTextFormatter.IsPlaceholderSessionTitle(Title))
        {
            Title = discovered.Title;
        }

        TranscriptPath ??= discovered.TranscriptPath;
        WorkingDirectory ??= discovered.WorkingDirectory;
        GitBranch ??= discovered.GitBranch;
        Model ??= discovered.Model;

        if (discovered.UpdatedAt > UpdatedAt)
        {
            UpdatedAt = discovered.UpdatedAt;
        }

        DiscoveredMessageCount = Math.Max(DiscoveredMessageCount, discovered.MessageCount);
    }

    public AcpSessionConfig BuildAttachConfigUnderGate(string defaultWorkingDirectory)
    {
        return new AcpSessionConfig
        {
            Provider = Provider,
            WorkingDirectory = WorkingDirectory ?? defaultWorkingDirectory,
            SessionId = SessionId,
            ResumeExisting = IsDiscoveredOnDisk || HasMobileAttachedOnce,
            Model = Model,
            AutoApproveTools = AutoApproveTools
        };
    }

    public AcpDiscoveredSession ToDiscoveredSessionUnderGate()
    {
        return new AcpDiscoveredSession(
            Provider: Provider,
            SessionId: SessionId,
            Title: Title,
            WorkingDirectory: WorkingDirectory,
            TranscriptPath: TranscriptPath!,
            UpdatedAt: UpdatedAt,
            CreatedAt: CreatedAt,
            Model: Model,
            GitBranch: GitBranch);
    }

    public DetachedLiveHandle DetachLiveHandleUnderGate()
    {
        var detached = new DetachedLiveHandle(LiveHandle, LivePumpCts, LivePumpTask);
        LiveHandle = null;
        LivePumpCts = null;
        LivePumpTask = null;
        return detached;
    }

    public void MarkMobileTurnStartingUnderGate(DateTimeOffset now, string? promptText = null)
    {
        UpdatedAt = now;
        LastMobileTurnActivityAt = now;
        MobileTurnStartingUntil = now.Add(AcpGatewayDefaults.MobileTurnStartingWindow);
        SuppressTranscriptTailUntilNextUserMessage = true;

        if (!string.IsNullOrWhiteSpace(promptText))
        {
            LastMobilePromptText = promptText.Trim();
            if (AcpTextFormatter.IsPlaceholderSessionTitle(Title))
            {
                Title = AcpTextFormatter.TruncateTitle(promptText);
            }
        }
    }

    public TranscriptTailDecision EvaluateTranscriptTailEventUnderGate(
        AcpEvent acpEvent,
        DateTimeOffset now)
    {
        if ((LiveHandle?.IsTurnActive ?? false) || now < MobileTurnStartingUntil)
        {
            return new TranscriptTailDecision(ShouldProcess: false);
        }

        if (acpEvent is AcpTranscriptHistoryEntryEvent { Role: AcpRoles.User } userEntry)
        {
            var cleanedUser = AcpTextFormatter.CleanUserTranscriptText(userEntry.Text);
            if (!string.IsNullOrWhiteSpace(LastMobilePromptText) &&
                string.Equals(cleanedUser, LastMobilePromptText, StringComparison.Ordinal))
            {
                LastMobilePromptText = null;
                return new TranscriptTailDecision(ShouldProcess: false);
            }

            SuppressTranscriptTailUntilNextUserMessage = false;
        }
        else if (SuppressTranscriptTailUntilNextUserMessage)
        {
            if ((now - LastMobileTurnCompletedAt) <= AcpGatewayDefaults.PostTurnTailSuppressionWindow)
            {
                return new TranscriptTailDecision(ShouldProcess: false);
            }

            SuppressTranscriptTailUntilNextUserMessage = false;
        }

        if (acpEvent is AcpTranscriptHistoryEntryEvent { Role: AcpRoles.Assistant } asstEntry &&
            !string.IsNullOrWhiteSpace(LastMobileAssistantText) &&
            string.Equals(asstEntry.Text?.Trim(), LastMobileAssistantText, StringComparison.Ordinal))
        {
            LastMobileAssistantText = null;
            return new TranscriptTailDecision(ShouldProcess: false);
        }

        LastDiskActivityAt = now;

        DetachedLiveHandle detachedOnExternalActivity = default;
        if (LiveHandle is not null)
        {
            HasExternalTranscriptActivitySinceAttach = true;
            detachedOnExternalActivity = DetachLiveHandleUnderGate();
        }

        return new TranscriptTailDecision(ShouldProcess: true, detachedOnExternalActivity);
    }

    public DetachedLiveHandle OnLiveEventAppliedUnderGate(AcpEvent acpEvent, DateTimeOffset now)
    {
        if (acpEvent is not AcpTurnCompletedEvent)
        {
            return default;
        }

        LastMobileTurnCompletedAt = now;
        MobileTurnStartingUntil = DateTimeOffset.MinValue;

        if (IsDiscoveredOnDisk &&
            LiveHandle is not null &&
            LiveHandle.GetQueuedPrompts().Count == 0 &&
            PendingPermissions.Count == 0)
        {
            return DetachLiveHandleUnderGate() with { PumpTask = null };
        }

        return default;
    }

    public void AddTimelineItemUnderGate(AcpGatewayTimelineItem item)
    {
        Timeline.Add(item);
        while (Timeline.Count > AcpGatewayDefaults.MaxTimelineItemsPerSession)
        {
            Timeline.RemoveAt(0);
        }
    }

    public AcpGatewaySessionSummary? AdvanceDiskActivityUnderGate(
        long currentLength,
        DateTimeOffset lastWriteUtc)
    {
        var lengthGrew = LastObservedFileLength > 0 && currentLength > LastObservedFileLength;
        var writeAdvanced = lastWriteUtc > UpdatedAt.AddSeconds(1);

        if (LastObservedFileLength == 0)
        {
            LastObservedFileLength = currentLength;
        }

        if (!lengthGrew && !writeAdvanced)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        LastObservedFileLength = currentLength;
        UpdatedAt = lastWriteUtc > now ? now : lastWriteUtc;
        if (lengthGrew)
        {
            LastDiskActivityAt = now;
        }

        return BuildSummaryUnderGate();
    }

    public async Task<AcpGatewaySessionSummary> BuildSummaryAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return BuildSummaryUnderGate();
        }
        finally
        {
            Gate.Release();
        }
    }

    public AcpGatewaySessionSummary BuildSummaryUnderGate()
    {
        var handle = LiveHandle;
        var queuedCount = handle?.GetQueuedPrompts().Count ?? 0;
        var now = DateTimeOffset.UtcNow;
        var recentDiskTurn = (now - LastDiskActivityAt) <= AcpGatewayDefaults.ActiveDiskTurnThreshold;
        var isTurnActive = (handle?.IsTurnActive ?? false) || recentDiskTurn;
        var isActiveNow = isTurnActive ||
                          handle is not null ||
                          (now - UpdatedAt) <= AcpGatewayDefaults.ActiveSessionRecencyThreshold;

        return new AcpGatewaySessionSummary
        {
            SessionKey = SessionKey,
            Provider = Provider.ToShortId(),
            ProviderDisplayName = Provider.ToDisplayName(),
            SessionId = SessionId,
            Title = Title,
            WorkingDirectory = WorkingDirectory,
            GitBranch = GitBranch,
            Model = Model,
            UpdatedAt = UpdatedAt,
            CreatedAt = CreatedAt,
            IsLiveAttached = handle is not null || HasMobileAttachedOnce,
            IsTurnActive = isTurnActive,
            IsActiveNow = isActiveNow,
            HasPendingPermission = PendingPermissions.Count > 0,
            QueuedPromptCount = queuedCount,
            MessageCount = Math.Max(DiscoveredMessageCount, Timeline.Count)
        };
    }

    public async Task<AcpGatewaySessionDetail> BuildDetailAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var summary = BuildSummaryUnderGate();
            var queued = LiveHandle?.GetQueuedPrompts() ?? [];
            var limit = Math.Max(AcpGatewayDefaults.DefaultTimelinePageSize, VisibleTimelineLimit);
            var page = AcpTimelineProjector.SliceTimelinePage(Timeline, limit);

            return new AcpGatewaySessionDetail
            {
                Summary = summary,
                Timeline = page.Items,
                TotalTimelineCount = page.TotalMessageCount,
                HiddenOlderCount = page.HiddenOlderCount,
                HasOlderMessages = page.HiddenOlderCount > 0,
                QueuedPrompts = queued,
                PendingPermissions = PendingPermissions.ToArray()
            };
        }
        finally
        {
            Gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? tailCts;
        Task? tailTask;
        DetachedLiveHandle detachedLive;

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            tailCts = TailCts;
            tailTask = TailTask;
            TailCts = null;
            TailTask = null;
            detachedLive = DetachLiveHandleUnderGate();
        }
        finally
        {
            Gate.Release();
        }

        if (tailCts is not null)
        {
            try
            {
                await tailCts.CancelAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore cancellation race
            }

            await WaitBackgroundTaskQuietlyAsync(tailTask).ConfigureAwait(false);
            tailCts.Dispose();
        }

        await detachedLive.DisposeAsync().ConfigureAwait(false);
    }

    internal static async Task WaitBackgroundTaskQuietlyAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(AcpGatewayDefaults.BackgroundTaskShutdownTimeout).ConfigureAwait(false);
        }
        catch
        {
            // Ignore cancellation or completion errors on teardown
        }
    }
}
