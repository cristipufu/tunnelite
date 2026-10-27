namespace Tunnelite.Acp;

internal static class TimelineItemKinds
{
    public const string Message = "message";
    public const string ToolCall = "tool_call";
    public const string SystemNote = "system_note";
    public const string Interruption = "interruption";
    public const string TurnCompleted = "turn_completed";
}

internal static class TimelineBadges
{
    public const string Steered = "STEERED";
    public const string Queued = "QUEUED";
    public const string Interrupt = "INTERRUPT";
}

internal static class RealtimeEnvelopeTypes
{
    public const string StateSnapshot = "state_snapshot";
    public const string SessionSummary = "session_summary";
    public const string TimelineItem = "timeline_item";
    public const string QueueUpdated = "queue_updated";
    public const string PermissionUpdated = "permission_updated";
    public const string ActionResult = "action_result";
    public const string ActionError = "action_error";
}

internal static class GatewayActions
{
    public const string SelectSession = "select_session";
    public const string LoadOlder = "load_older";
    public const string RefreshSessions = "refresh_sessions";
    public const string ProbeUsage = "probe_usage";
    public const string CreateSession = "create_session";
    public const string SendPrompt = "send_prompt";
    public const string Steer = "steer";
    public const string EnqueuePrompt = "enqueue_prompt";
    public const string RemoveQueued = "remove_queued";
    public const string ClearQueued = "clear_queued";
    public const string InterruptAndPrompt = "interrupt_and_prompt";
    public const string CancelTurn = "cancel_turn";
    public const string RespondPermission = "respond_permission";
    public const string SetModel = "set_model";
}

internal static class AcpGatewayDefaults
{
    public const int DefaultTimelinePageSize = 30;
    public const int TimelinePageStep = 40;
    public const int MaxTimelineItemsPerSession = 2000;
    public const int MaxToolPayloadChars = 12000;
    public const int MaxToolSummaryChars = 200;
    public const int MaxActivityLogEntries = 60;
    public const int DefaultThoughtPreviewChars = 110;
    public const int MaxStreamingThoughtBufferChars = 1600;
    public const int MaxRecentDirectories = 8;
    public const int MaxDiscoveredSessionsPerProvider = 30;

    public static readonly TimeSpan MonitorLoopInterval = TimeSpan.FromSeconds(1.0);
    public const int DiscoveryRefreshEveryMonitorTicks = 8;
    public static readonly TimeSpan MobileTurnStartingWindow = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PostTurnTailSuppressionWindow = TimeSpan.FromSeconds(2.5);
    public static readonly TimeSpan ActiveDiskTurnThreshold = TimeSpan.FromSeconds(35);
    public static readonly TimeSpan CompletedAssistantTurnGraceThreshold = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan ActiveSessionRecencyThreshold = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan BackgroundTaskShutdownTimeout = TimeSpan.FromMilliseconds(500);
}
