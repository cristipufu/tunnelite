using Acplite.Sdk.Protocol;

namespace Tunnelite.Acp;

internal readonly record struct SessionEventProjection(
    AcpGatewayTimelineItem? PreceedingTimelineItem = null,
    AcpGatewayTimelineItem? TimelineItem = null,
    IReadOnlyList<AcpQueuedPromptItem>? QueuedPrompts = null,
    IReadOnlyList<AcpGatewayPendingPermission>? PendingPermissions = null);

internal readonly record struct ToolActivityUpdate(
    DateTimeOffset Timestamp,
    string? ToolName,
    string? SummaryText,
    string? ToolCallId,
    string ToolKind,
    string ToolStatus,
    string? RawInputJson = null,
    string? RawOutput = null,
    bool IsUpdateToSameToolCallId = false,
    string? ThoughtPreview = null);

internal readonly record struct TimelinePageSlice(
    IReadOnlyList<AcpGatewayTimelineItem> Items,
    int TotalMessageCount,
    int HiddenOlderCount);

internal static class AcpTimelineProjector
{
    public static SessionEventProjection ApplyEventUnderGate(
        AcpManagedSession session,
        AcpEvent acpEvent,
        bool isHistoricalLoad,
        Func<string> nextItemId)
    {
        if (!isHistoricalLoad || acpEvent.Timestamp > session.UpdatedAt)
        {
            session.UpdatedAt = acpEvent.Timestamp;
        }

        return acpEvent switch
        {
            AcpSessionStartedEvent started => ApplySessionStarted(session, started),
            AcpTranscriptHistoryEntryEvent hist => ApplyTranscriptHistoryEntry(session, hist, isHistoricalLoad, nextItemId),
            AcpThinkingDeltaEvent thinkingDelta => ApplyThinkingDelta(session, thinkingDelta, nextItemId),
            AcpAssistantTextDeltaEvent textDelta => ApplyAssistantTextDelta(session, textDelta, nextItemId),
            AcpToolCallEvent toolCall => ApplyToolCall(session, toolCall, nextItemId),
            AcpPermissionRequestEvent perm => ApplyPermissionRequest(session, perm),
            AcpPromptQueueUpdatedEvent queueUpdated => ApplyPromptQueueUpdated(session, queueUpdated, nextItemId),
            AcpInterruptionEvent interruption => ApplyInterruption(session, interruption, nextItemId),
            AcpTurnCompletedEvent turnCompleted => ApplyTurnCompleted(session, turnCompleted, nextItemId),
            _ => default
        };
    }

    public static TimelinePageSlice SliceTimelinePage(
        List<AcpGatewayTimelineItem> timeline,
        int messageLimit)
    {
        var totalMessages = CountMessages(timeline);
        if (totalMessages <= messageLimit)
        {
            var totalCount = totalMessages > 0 ? totalMessages : timeline.Count;
            return new TimelinePageSlice(timeline.ToArray(), totalCount, HiddenOlderCount: 0);
        }

        var (startIndex, messagesSeen) = FindPageStartIndex(timeline, messageLimit);
        var hiddenOlderMessages = Math.Max(0, totalMessages - messagesSeen);
        var slice = timeline.GetRange(startIndex, timeline.Count - startIndex).ToArray();
        return new TimelinePageSlice(slice, totalMessages, hiddenOlderMessages);
    }

    private static int CountMessages(List<AcpGatewayTimelineItem> timeline)
    {
        var count = 0;
        for (var i = 0; i < timeline.Count; i++)
        {
            if (timeline[i].Kind == TimelineItemKinds.Message)
            {
                count++;
            }
        }
        return count;
    }

    private static (int StartIndex, int MessagesSeen) FindPageStartIndex(
        List<AcpGatewayTimelineItem> timeline,
        int messageLimit)
    {
        var messagesSeen = 0;
        for (var i = timeline.Count - 1; i >= 0; i--)
        {
            if (timeline[i].Kind != TimelineItemKinds.Message)
            {
                continue;
            }

            messagesSeen++;
            if (messagesSeen == messageLimit)
            {
                var startIndex = i;
                if (startIndex > 0 &&
                    timeline[startIndex - 1].Kind == TimelineItemKinds.ToolCall &&
                    timeline[startIndex].Role == AcpRoles.Assistant)
                {
                    startIndex--;
                }
                return (startIndex, messagesSeen);
            }
        }

        return (0, messagesSeen);
    }

    private static SessionEventProjection ApplySessionStarted(
        AcpManagedSession session,
        AcpSessionStartedEvent started)
    {
        if (!string.IsNullOrWhiteSpace(started.Model))
        {
            session.Model = started.Model;
        }

        if (!string.IsNullOrWhiteSpace(started.WorkingDirectory))
        {
            session.WorkingDirectory = started.WorkingDirectory;
        }

        return default;
    }

    private static SessionEventProjection ApplyTranscriptHistoryEntry(
        AcpManagedSession session,
        AcpTranscriptHistoryEntryEvent hist,
        bool isHistoricalLoad,
        Func<string> nextItemId)
    {
        var cleanedText = hist.Role == AcpRoles.User
            ? AcpTextFormatter.CleanUserTranscriptText(hist.Text)
            : hist.Text;

        if (!isHistoricalLoad &&
            hist.Role == AcpRoles.User &&
            !string.IsNullOrWhiteSpace(session.LastMobilePromptText) &&
            string.Equals(cleanedText, session.LastMobilePromptText, StringComparison.Ordinal))
        {
            session.LastMobilePromptText = null;
            return default;
        }

        if (string.IsNullOrWhiteSpace(cleanedText) &&
            string.IsNullOrWhiteSpace(hist.Thinking) &&
            string.IsNullOrWhiteSpace(hist.ToolName))
        {
            return default;
        }

        var hasProseText = !string.IsNullOrWhiteSpace(cleanedText) &&
                           hist.Role is not (AcpRoles.Tool or AcpRoles.System) &&
                           !string.Equals(cleanedText, hist.ToolName, StringComparison.OrdinalIgnoreCase);

        var thoughtStream = hist.Role != AcpRoles.User
            ? AcpTextFormatter.CleanThoughtStream(hist.Thinking)
            : null;

        if (!hasProseText && hist.Role != AcpRoles.User)
        {
            return ApplyNonProseHistoryStep(session, hist, cleanedText, thoughtStream, isHistoricalLoad, nextItemId);
        }

        AcpGatewayTimelineItem? settledActivity = null;
        if (hist.Role == AcpRoles.Assistant)
        {
            if (!string.IsNullOrWhiteSpace(hist.Thinking) && string.IsNullOrWhiteSpace(hist.ToolName))
            {
                UpsertInlineThoughtUnderGate(
                    session,
                    hist.Timestamp,
                    hist.Thinking,
                    isStreamingDelta: false,
                    AcpToolStatuses.InProgress,
                    nextItemId);
            }
            settledActivity = CompleteInlineActivityUnderGate(session);
        }

        var messageItem = new AcpGatewayTimelineItem
        {
            Id = nextItemId(),
            Kind = TimelineItemKinds.Message,
            Role = hist.Role,
            Timestamp = hist.Timestamp,
            Text = cleanedText,
            ToolName = hist.ToolName,
            ToolCallId = hist.ToolCallId
        };
        session.AddTimelineItemUnderGate(messageItem);
        var primaryItem = messageItem;

        if (!string.IsNullOrWhiteSpace(hist.ToolName) && hist.Role != AcpRoles.User)
        {
            primaryItem = UpsertInlineActivityUnderGate(
                session,
                new ToolActivityUpdate(
                    Timestamp: hist.Timestamp,
                    ToolName: hist.ToolName,
                    SummaryText: AcpTextFormatter.NormalizeToolText(hist.ToolName, null),
                    ToolCallId: hist.ToolCallId,
                    ToolKind: AcpToolKinds.Command,
                    ToolStatus: AcpToolStatuses.Completed,
                    ThoughtPreview: thoughtStream),
                nextItemId);
        }

        return new SessionEventProjection(
            PreceedingTimelineItem: settledActivity,
            TimelineItem: primaryItem);
    }

    private static SessionEventProjection ApplyNonProseHistoryStep(
        AcpManagedSession session,
        AcpTranscriptHistoryEntryEvent hist,
        string? cleanedText,
        string? thoughtPreview,
        bool isHistoricalLoad,
        Func<string> nextItemId)
    {
        var stepStatus = isHistoricalLoad ? AcpToolStatuses.Completed : AcpToolStatuses.InProgress;

        if (!string.IsNullOrWhiteSpace(hist.ToolName))
        {
            var rawOut = !string.IsNullOrWhiteSpace(cleanedText) &&
                         !string.Equals(cleanedText, hist.ToolName, StringComparison.OrdinalIgnoreCase)
                ? AcpTextFormatter.TruncateLargePayload(cleanedText, AcpGatewayDefaults.MaxToolPayloadChars)
                : null;
            var summaryText = AcpTextFormatter.NormalizeToolText(hist.ToolName, cleanedText);

            var activityItem = UpsertInlineActivityUnderGate(
                session,
                new ToolActivityUpdate(
                    Timestamp: hist.Timestamp,
                    ToolName: hist.ToolName,
                    SummaryText: summaryText,
                    ToolCallId: hist.ToolCallId,
                    ToolKind: AcpToolKinds.Command,
                    ToolStatus: stepStatus,
                    RawOutput: rawOut,
                    ThoughtPreview: thoughtPreview),
                nextItemId);
            return new SessionEventProjection(TimelineItem: activityItem);
        }

        if (!string.IsNullOrWhiteSpace(hist.Thinking))
        {
            var thoughtItem = UpsertInlineThoughtUnderGate(
                session,
                hist.Timestamp,
                hist.Thinking,
                isStreamingDelta: false,
                stepStatus,
                nextItemId);
            return new SessionEventProjection(TimelineItem: thoughtItem);
        }

        return default;
    }

    private static SessionEventProjection ApplyThinkingDelta(
        AcpManagedSession session,
        AcpThinkingDeltaEvent thinkingDelta,
        Func<string> nextItemId)
    {
        var item = UpsertInlineThoughtUnderGate(
            session,
            thinkingDelta.Timestamp,
            thinkingDelta.Delta,
            isStreamingDelta: true,
            AcpToolStatuses.InProgress,
            nextItemId);
        return new SessionEventProjection(TimelineItem: item);
    }

    private static SessionEventProjection ApplyAssistantTextDelta(
        AcpManagedSession session,
        AcpAssistantTextDeltaEvent textDelta,
        Func<string> nextItemId)
    {
        var settledActivity = CompleteInlineActivityUnderGate(session);
        var last = session.Timeline.Count > 0 ? session.Timeline[^1] : null;

        if (last is not null &&
            last.Role == AcpRoles.Assistant &&
            last.Kind == TimelineItemKinds.Message)
        {
            var updated = last with
            {
                Text = (last.Text ?? string.Empty) + textDelta.Delta,
                Timestamp = textDelta.Timestamp
            };
            session.Timeline[^1] = updated;
            session.LastMobileAssistantText = updated.Text?.Trim();
            return new SessionEventProjection(
                PreceedingTimelineItem: settledActivity,
                TimelineItem: updated);
        }

        var created = new AcpGatewayTimelineItem
        {
            Id = nextItemId(),
            Kind = TimelineItemKinds.Message,
            Role = AcpRoles.Assistant,
            Timestamp = textDelta.Timestamp,
            Text = textDelta.Delta
        };
        session.AddTimelineItemUnderGate(created);
        session.LastMobileAssistantText = created.Text?.Trim();
        return new SessionEventProjection(
            PreceedingTimelineItem: settledActivity,
            TimelineItem: created);
    }

    private static SessionEventProjection ApplyToolCall(
        AcpManagedSession session,
        AcpToolCallEvent toolCall,
        Func<string> nextItemId)
    {
        var last = session.Timeline.Count > 0 ? session.Timeline[^1] : null;
        var isSameCallId = last is not null &&
                           last.Kind == TimelineItemKinds.ToolCall &&
                           !string.IsNullOrWhiteSpace(toolCall.ToolCallId) &&
                           string.Equals(last.ToolCallId, toolCall.ToolCallId, StringComparison.Ordinal);

        var item = UpsertInlineActivityUnderGate(
            session,
            new ToolActivityUpdate(
                Timestamp: toolCall.Timestamp,
                ToolName: toolCall.ToolName,
                SummaryText: AcpTextFormatter.NormalizeToolText(toolCall.ToolName, toolCall.Title),
                ToolCallId: toolCall.ToolCallId,
                ToolKind: toolCall.Kind ?? AcpToolKinds.Command,
                ToolStatus: toolCall.Status,
                RawInputJson: toolCall.RawInputJson,
                RawOutput: toolCall.RawOutput,
                IsUpdateToSameToolCallId: isSameCallId),
            nextItemId);

        return new SessionEventProjection(TimelineItem: item);
    }

    private static SessionEventProjection ApplyPermissionRequest(
        AcpManagedSession session,
        AcpPermissionRequestEvent perm)
    {
        if (perm.Options.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(perm.RequestId))
            {
                session.PendingPermissions.Clear();
            }
            else
            {
                session.PendingPermissions.RemoveAll(p => string.Equals(p.RequestId, perm.RequestId, StringComparison.Ordinal));
            }
            return new SessionEventProjection(PendingPermissions: session.PendingPermissions.ToArray());
        }

        var pending = new AcpGatewayPendingPermission
        {
            RequestId = perm.RequestId,
            SessionKey = session.SessionKey,
            Provider = session.Provider.ToShortId(),
            ToolName = perm.ToolName,
            Title = perm.Title,
            Description = perm.Description,
            RawInputJson = AcpTextFormatter.TruncateLargePayload(perm.RawInputJson, AcpGatewayDefaults.MaxToolPayloadChars),
            Options = perm.Options,
            RequestedAt = perm.Timestamp
        };

        session.PendingPermissions.RemoveAll(p => string.Equals(p.RequestId, perm.RequestId, StringComparison.Ordinal));
        session.PendingPermissions.Add(pending);
        return new SessionEventProjection(PendingPermissions: session.PendingPermissions.ToArray());
    }

    private static SessionEventProjection ApplyPromptQueueUpdated(
        AcpManagedSession session,
        AcpPromptQueueUpdatedEvent queueUpdated,
        Func<string> nextItemId)
    {
        AcpGatewayTimelineItem? autoDrainedUserItem = null;
        if (session.LastKnownQueue.Count > queueUpdated.QueuedPrompts.Count &&
            session.LastKnownQueue.Count > 0)
        {
            var previousHead = session.LastKnownQueue[0];
            var stillPresent = queueUpdated.QueuedPrompts.Any(
                q => string.Equals(q.QueueItemId, previousHead.QueueItemId, StringComparison.Ordinal));
            if (!stillPresent && session.LastTurnJustCompleted)
            {
                autoDrainedUserItem = new AcpGatewayTimelineItem
                {
                    Id = nextItemId(),
                    Kind = TimelineItemKinds.Message,
                    Role = AcpRoles.User,
                    Timestamp = queueUpdated.Timestamp,
                    Text = previousHead.Prompt,
                    Badge = TimelineBadges.Queued
                };
                session.AddTimelineItemUnderGate(autoDrainedUserItem);
            }
        }

        session.LastTurnJustCompleted = false;
        session.LastKnownQueue = queueUpdated.QueuedPrompts;
        return new SessionEventProjection(
            PreceedingTimelineItem: autoDrainedUserItem,
            QueuedPrompts: queueUpdated.QueuedPrompts);
    }

    private static SessionEventProjection ApplyInterruption(
        AcpManagedSession session,
        AcpInterruptionEvent interruption,
        Func<string> nextItemId)
    {
        var item = new AcpGatewayTimelineItem
        {
            Id = nextItemId(),
            Kind = TimelineItemKinds.Interruption,
            Role = AcpRoles.System,
            Timestamp = interruption.Timestamp,
            Text = interruption.Classification.Message,
            Badge = interruption.Classification.Kind.ToString(),
            IsError = true,
            IsRetryable = interruption.Classification.IsRetryable
        };
        session.AddTimelineItemUnderGate(item);
        return new SessionEventProjection(TimelineItem: item);
    }

    private static SessionEventProjection ApplyTurnCompleted(
        AcpManagedSession session,
        AcpTurnCompletedEvent turnCompleted,
        Func<string> nextItemId)
    {
        var settledActivity = CompleteInlineActivityUnderGate(session);
        session.LastTurnJustCompleted = true;

        var item = new AcpGatewayTimelineItem
        {
            Id = nextItemId(),
            Kind = TimelineItemKinds.TurnCompleted,
            Role = AcpRoles.System,
            Timestamp = turnCompleted.Timestamp,
            StopReason = turnCompleted.StopReason,
            InputTokens = turnCompleted.InputTokens,
            OutputTokens = turnCompleted.OutputTokens,
            CostUsd = turnCompleted.CostUsd,
            DurationMs = turnCompleted.DurationMs,
            IsError = string.Equals(turnCompleted.StopReason, AcpStopReasons.Error, StringComparison.OrdinalIgnoreCase)
        };
        session.AddTimelineItemUnderGate(item);
        return new SessionEventProjection(
            PreceedingTimelineItem: settledActivity,
            TimelineItem: item);
    }

    private static AcpGatewayTimelineItem? UpsertInlineThoughtUnderGate(
        AcpManagedSession session,
        DateTimeOffset timestamp,
        string? rawThinking,
        bool isStreamingDelta,
        string toolStatus,
        Func<string> nextItemId)
    {
        if (string.IsNullOrWhiteSpace(rawThinking))
        {
            return null;
        }

        var combined = isStreamingDelta
            ? (session.CurrentStreamingThoughtBuffer ?? string.Empty) + rawThinking
            : rawThinking;
        session.CurrentStreamingThoughtBuffer = isStreamingDelta
            ? AcpTextFormatter.TruncateLargePayload(combined, AcpGatewayDefaults.MaxStreamingThoughtBufferChars)
            : null;

        var previewWindow = AcpTextFormatter.FormatThoughtPreview(combined, AcpGatewayDefaults.DefaultThoughtPreviewChars);
        var cleanedStream = AcpTextFormatter.CleanThoughtStream(combined) ?? previewWindow;
        if (string.IsNullOrWhiteSpace(previewWindow))
        {
            return session.Timeline.Count > 0 && session.Timeline[^1].Kind == TimelineItemKinds.ToolCall
                ? session.Timeline[^1]
                : null;
        }

        var last = session.Timeline.Count > 0 ? session.Timeline[^1] : null;
        if (last is not null && last.Kind == TimelineItemKinds.ToolCall)
        {
            var hasCommands = last.ActivityLog is { Count: > 0 };
            var updated = hasCommands
                ? last with
                {
                    Timestamp = timestamp,
                    ToolStatus = toolStatus,
                    Thinking = cleanedStream
                }
                : last with
                {
                    Timestamp = timestamp,
                    ToolName = "thinking",
                    ToolKind = AcpToolKinds.Thought,
                    ToolStatus = toolStatus,
                    Text = previewWindow,
                    Thinking = cleanedStream
                };
            session.Timeline[^1] = updated;
            return updated;
        }

        var created = new AcpGatewayTimelineItem
        {
            Id = nextItemId(),
            Kind = TimelineItemKinds.ToolCall,
            Role = AcpRoles.Assistant,
            Timestamp = timestamp,
            ToolName = "thinking",
            ToolKind = AcpToolKinds.Thought,
            ToolStatus = toolStatus,
            Text = previewWindow,
            StepCount = 0,
            ActivityLog = [],
            Thinking = cleanedStream
        };
        session.AddTimelineItemUnderGate(created);
        return created;
    }

    private static AcpGatewayTimelineItem? CompleteInlineActivityUnderGate(AcpManagedSession session)
    {
        session.CurrentStreamingThoughtBuffer = null;

        if (session.Timeline.Count == 0 || session.Timeline[^1].Kind != TimelineItemKinds.ToolCall)
        {
            return null;
        }

        var lastTool = session.Timeline[^1];
        if (string.Equals(lastTool.ToolKind, AcpToolKinds.Thought, StringComparison.Ordinal) &&
            lastTool.ActivityLog is { Count: > 0 } logs)
        {
            var (cmdName, cmdTarget) = AcpTextFormatter.ParseActivityLogEntry(logs[^1]);
            var settled = lastTool with
            {
                ToolName = cmdName,
                Text = cmdTarget,
                ToolKind = AcpToolKinds.Command,
                ToolStatus = AcpToolStatuses.Completed
            };
            session.Timeline[^1] = settled;
            return settled;
        }

        if (!string.Equals(lastTool.ToolStatus, AcpToolStatuses.Completed, StringComparison.OrdinalIgnoreCase))
        {
            var settled = lastTool with
            {
                ToolStatus = AcpToolStatuses.Completed
            };
            session.Timeline[^1] = settled;
            return settled;
        }

        return null;
    }

    private static AcpGatewayTimelineItem UpsertInlineActivityUnderGate(
        AcpManagedSession session,
        ToolActivityUpdate update,
        Func<string> nextItemId)
    {
        session.CurrentStreamingThoughtBuffer = null;

        var cleanToolName = string.IsNullOrWhiteSpace(update.ToolName) ? AcpRoles.Tool : update.ToolName.Trim();
        var logLine = AcpTextFormatter.FormatActivityLogEntry(cleanToolName, update.SummaryText);
        var truncatedInput = AcpTextFormatter.TruncateLargePayload(update.RawInputJson, AcpGatewayDefaults.MaxToolPayloadChars);
        var truncatedOutput = AcpTextFormatter.TruncateLargePayload(update.RawOutput, AcpGatewayDefaults.MaxToolPayloadChars);

        var last = session.Timeline.Count > 0 ? session.Timeline[^1] : null;
        if (last is not null && last.Kind == TimelineItemKinds.ToolCall)
        {
            var (logs, nextStepCount) = AppendOrUpdateActivityLog(
                last.ActivityLog,
                last.StepCount,
                logLine,
                update.IsUpdateToSameToolCallId);

            var updated = last with
            {
                Timestamp = update.Timestamp,
                ToolName = cleanToolName,
                Text = !string.IsNullOrWhiteSpace(update.SummaryText)
                    ? update.SummaryText
                    : (update.IsUpdateToSameToolCallId ? last.Text : null),
                ToolCallId = update.ToolCallId ?? last.ToolCallId,
                ToolKind = update.ToolKind,
                ToolStatus = update.ToolStatus,
                StepCount = nextStepCount,
                ActivityLog = logs,
                Thinking = !string.IsNullOrWhiteSpace(update.ThoughtPreview) ? update.ThoughtPreview : last.Thinking,
                RawInputJson = truncatedInput ?? last.RawInputJson,
                RawOutput = truncatedOutput ?? last.RawOutput
            };
            session.Timeline[^1] = updated;
            return updated;
        }

        var created = new AcpGatewayTimelineItem
        {
            Id = nextItemId(),
            Kind = TimelineItemKinds.ToolCall,
            Role = AcpRoles.Assistant,
            Timestamp = update.Timestamp,
            Text = update.SummaryText,
            ToolName = cleanToolName,
            ToolCallId = update.ToolCallId,
            ToolKind = update.ToolKind,
            ToolStatus = update.ToolStatus,
            StepCount = 1,
            ActivityLog = [logLine],
            Thinking = update.ThoughtPreview,
            RawInputJson = truncatedInput,
            RawOutput = truncatedOutput
        };
        session.AddTimelineItemUnderGate(created);
        return created;
    }

    private static (List<string> Logs, int StepCount) AppendOrUpdateActivityLog(
        IReadOnlyList<string>? existingLog,
        int currentStepCount,
        string logLine,
        bool isUpdateToSameToolCallId)
    {
        var logs = new List<string>(existingLog ?? []);
        var nextStepCount = currentStepCount;

        if (isUpdateToSameToolCallId && logs.Count > 0)
        {
            logs[^1] = logLine;
            nextStepCount = Math.Max(1, nextStepCount);
        }
        else
        {
            logs.Add(logLine);
            nextStepCount++;
        }

        while (logs.Count > AcpGatewayDefaults.MaxActivityLogEntries)
        {
            logs.RemoveAt(0);
        }

        return (logs, nextStepCount);
    }
}
