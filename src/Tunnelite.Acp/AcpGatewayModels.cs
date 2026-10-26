using System.Text.Json.Serialization;
using Acplite.Sdk.Protocol;

namespace Tunnelite.Acp;

public sealed record AcpGatewaySessionSummary
{
    public required string SessionKey { get; init; }
    public required string Provider { get; init; }
    public required string ProviderDisplayName { get; init; }
    public required string SessionId { get; init; }
    public required string Title { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? GitBranch { get; init; }
    public string? Model { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public bool IsLiveAttached { get; init; }
    public bool IsTurnActive { get; init; }
    public bool IsActiveNow { get; init; }
    public bool HasPendingPermission { get; init; }
    public int QueuedPromptCount { get; init; }
    public int MessageCount { get; init; }
}

public sealed record AcpGatewayTimelineItem
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Role { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? Text { get; init; }
    public string? Thinking { get; init; }
    public string? Badge { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public string? ToolKind { get; init; }
    public string? ToolStatus { get; init; }
    public int StepCount { get; init; } = 1;
    public IReadOnlyList<string>? ActivityLog { get; init; }
    public string? RawInputJson { get; init; }
    public string? RawOutput { get; init; }
    public string? StopReason { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public decimal? CostUsd { get; init; }
    public long? DurationMs { get; init; }
    public bool IsError { get; init; }
    public bool IsRetryable { get; init; }
}

public sealed record AcpGatewayPendingPermission
{
    public required string RequestId { get; init; }
    public required string SessionKey { get; init; }
    public required string Provider { get; init; }
    public required string ToolName { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string? RawInputJson { get; init; }
    public required IReadOnlyList<AcpPermissionOption> Options { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
}

public sealed record AcpGatewaySessionDetail
{
    public required AcpGatewaySessionSummary Summary { get; init; }
    public required IReadOnlyList<AcpGatewayTimelineItem> Timeline { get; init; }
    public int TotalTimelineCount { get; init; }
    public int HiddenOlderCount { get; init; }
    public bool HasOlderMessages { get; init; }
    public required IReadOnlyList<AcpQueuedPromptItem> QueuedPrompts { get; init; }
    public required IReadOnlyList<AcpGatewayPendingPermission> PendingPermissions { get; init; }
}

public sealed record AcpGatewayStateSnapshot
{
    public required string MachineName { get; init; }
    public required string DefaultWorkingDirectory { get; init; }
    public string? ScopedProvider { get; init; }
    public required IReadOnlyList<AcpProviderDescriptor> Providers { get; init; }
    public required IReadOnlyList<AcpUsageSnapshot> UsageSnapshots { get; init; }
    public required IReadOnlyList<AcpGatewaySessionSummary> Sessions { get; init; }
    public required IReadOnlyList<string> RecentDirectories { get; init; }
    public string? ActiveSessionKey { get; init; }
    public AcpGatewaySessionDetail? ActiveSession { get; init; }
}

public sealed class AcpGatewayActionRequest
{
    public string Action { get; set; } = "";
    public string? SessionKey { get; set; }
    public string? Provider { get; set; }
    public string? SessionId { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? Model { get; set; }
    public string? Prompt { get; set; }
    public string? QueueItemId { get; set; }
    public string? PermissionRequestId { get; set; }
    public bool? Approved { get; set; }
    public string? SelectedOptionId { get; set; }
    public bool? AutoApproveTools { get; set; }
    public int? TimelineLimit { get; set; }
}

public sealed record AcpGatewayActionResponse
{
    public required bool Ok { get; init; }
    public string? Message { get; init; }
    public string? SessionKey { get; init; }
    public AcpQueuedPromptItem? QueuedItem { get; init; }
    public AcpGatewayStateSnapshot? State { get; init; }
    public AcpGatewaySessionDetail? Session { get; init; }
}

public sealed record AcpGatewayRealtimeEnvelope
{
    public required string Type { get; init; }
    public string? SessionKey { get; init; }
    public AcpGatewayStateSnapshot? State { get; init; }
    public AcpGatewaySessionDetail? Session { get; init; }
    public AcpGatewaySessionSummary? Summary { get; init; }
    public AcpGatewayTimelineItem? TimelineItem { get; init; }
    public IReadOnlyList<AcpQueuedPromptItem>? QueuedPrompts { get; init; }
    public IReadOnlyList<AcpGatewayPendingPermission>? PendingPermissions { get; init; }
    public string? Message { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(AcpGatewaySessionSummary))]
[JsonSerializable(typeof(IReadOnlyList<AcpGatewaySessionSummary>))]
[JsonSerializable(typeof(AcpGatewayTimelineItem))]
[JsonSerializable(typeof(IReadOnlyList<AcpGatewayTimelineItem>))]
[JsonSerializable(typeof(AcpGatewayPendingPermission))]
[JsonSerializable(typeof(IReadOnlyList<AcpGatewayPendingPermission>))]
[JsonSerializable(typeof(AcpGatewaySessionDetail))]
[JsonSerializable(typeof(AcpGatewayStateSnapshot))]
[JsonSerializable(typeof(AcpGatewayActionRequest))]
[JsonSerializable(typeof(AcpGatewayActionResponse))]
[JsonSerializable(typeof(AcpGatewayRealtimeEnvelope))]
[JsonSerializable(typeof(AcpProviderDescriptor))]
[JsonSerializable(typeof(IReadOnlyList<AcpProviderDescriptor>))]
[JsonSerializable(typeof(AcpUsageSnapshot))]
[JsonSerializable(typeof(IReadOnlyList<AcpUsageSnapshot>))]
[JsonSerializable(typeof(AcpQueuedPromptItem))]
[JsonSerializable(typeof(IReadOnlyList<AcpQueuedPromptItem>))]
[JsonSerializable(typeof(AcpPermissionOption))]
[JsonSerializable(typeof(IReadOnlyList<AcpPermissionOption>))]
public partial class AcpGatewayJsonContext : JsonSerializerContext
{
}
