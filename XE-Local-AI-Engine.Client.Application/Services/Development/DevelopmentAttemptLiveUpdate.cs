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
    Terminal,

    /// <summary>A batch of a running command's redacted output lines in <see cref="DevelopmentAttemptLiveUpdate.OutputDelta" />; lossy.</summary>
    CommandOutput
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

    /// <summary>The sandbox execution id of the running command; the stop route targets it.</summary>
    public string? CurrentCommandExecutionId { get; init; }

    /// <summary>On a command update: all output lines of that command not shown live so far (cumulative).</summary>
    public int? CommandOutputSkippedLines { get; init; }

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

    internal bool IsReplaceable =>
        Kind is DevelopmentAttemptLiveUpdateKind.Output
            or DevelopmentAttemptLiveUpdateKind.Activity
            or DevelopmentAttemptLiveUpdateKind.Metrics
            or DevelopmentAttemptLiveUpdateKind.Progress
            or DevelopmentAttemptLiveUpdateKind.CommandOutput;
}

public sealed class DevelopmentAttemptLiveSnapshot
{
    public required Guid AttemptId { get; init; }

    public required long Watermark { get; init; }

    public required long DroppedOrCoalescedUpdateCount { get; init; }

    public required DevelopmentAttemptLiveUpdate? Latest { get; init; }
}
