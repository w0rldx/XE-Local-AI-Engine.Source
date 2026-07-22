namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Persistence.Entities;

public enum DevelopmentAttemptLiveUpdateKind
{
    Output,
    Activity,
    Tool,
    Command,
    Metrics,
    Progress,
    Warning,
    Terminal
}

public sealed record DevelopmentAttemptLiveUpdate
{
    public required Guid ProjectId { get; init; }
    public required Guid TaskId { get; init; }
    public required Guid AttemptId { get; init; }
    public long Sequence { get; init; }
    public long OccurredAtUtc { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required DevelopmentAttemptLiveUpdateKind Kind { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required DevelopmentAttemptRole Role { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required DevelopmentAttemptStatus Status { get; init; }
    public required string ModelId { get; init; }
    public required string Provider { get; init; }
    public string? OutputDelta { get; init; }
    public string? CurrentActivity { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? ReasoningTokens { get; init; }
    public double? OutputTokensPerSecond { get; init; }
    public int ProviderRoundCount { get; init; }
    public int ToolCallCount { get; init; }
    public int CommandCount { get; init; }
    public string? CurrentToolId { get; init; }
    public string? CurrentCommandId { get; init; }
    public long? CurrentOperationElapsedMilliseconds { get; init; }
    public int ChangedFileCount { get; init; }
    public long PatchByteCount { get; init; }
    public string? SubjectHash { get; init; }
    public double? ContextUsagePercent { get; init; }
    public double? ContextHeadroomPercent { get; init; }
    public long SecondsSinceMeaningfulProgress { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DevelopmentProgressWarningCategory? WarningCategory { get; init; }
    public string? WarningMessage { get; init; }

    internal bool IsReplaceable => Kind is DevelopmentAttemptLiveUpdateKind.Output
        or DevelopmentAttemptLiveUpdateKind.Activity
        or DevelopmentAttemptLiveUpdateKind.Metrics
        or DevelopmentAttemptLiveUpdateKind.Progress;
}

public sealed record DevelopmentAttemptLiveSnapshot(
    Guid AttemptId,
    long Watermark,
    long DroppedOrCoalescedUpdateCount,
    DevelopmentAttemptLiveUpdate? Latest);
