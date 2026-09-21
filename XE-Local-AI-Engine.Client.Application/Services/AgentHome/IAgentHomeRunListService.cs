namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Reads the on-disk AgentHome run history, newest first, a page at a time. There is no database row for a run:
///     the runs directory IS the index, and every field below comes from that run's own files.
/// </summary>
public interface IAgentHomeRunListService
{
    Task<AgentHomeRunPage> ListAsync(int limit, int offset, CancellationToken cancellationToken = default);
}

/// <summary>One page of run summaries plus the unpaged total, so a client can render a real pager.</summary>
public sealed record AgentHomeRunPage
{
    public required IReadOnlyList<AgentHomeRunSummary> Items { get; init; }

    public required int TotalCount { get; init; }
}

/// <summary>
///     What one run looks like in a list. Every value is either node-minted or a closed token this service maps a
///     run's log onto; no host path, no patch content and no command output reaches it.
/// </summary>
public sealed record AgentHomeRunSummary
{
    /// <summary>The node-minted run id, also the directory name.</summary>
    public required string RunId { get; init; }

    /// <summary>When the node minted the run id.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>
    ///     One of <see cref="AgentHomeRunOutcomes" />. A run whose log is missing, truncated or unparseable reads as
    ///     <see cref="AgentHomeRunOutcomes.Unknown" /> rather than breaking the page.
    /// </summary>
    public required string Outcome { get; init; }

    /// <summary>Whether the run exported a <c>changes.patch</c>.</summary>
    public required bool PatchExported { get; init; }

    /// <summary>How many files the export recorded, or <see langword="null" /> when that could not be read.</summary>
    public int? ChangedFileCount { get; init; }

    /// <summary>One of <see cref="AgentHomeRunApplyStates" />, read from the run's own apply events.</summary>
    public required string ApplyState { get; init; }

    /// <summary>The conversation the run was started from, when the run log recorded one.</summary>
    public Guid? ConversationId { get; init; }

    /// <summary>Bytes the run occupies on disk, links counted as themselves and never followed.</summary>
    public required long SizeBytes { get; init; }
}

/// <summary>The closed outcome vocabulary a run summary may carry.</summary>
public static class AgentHomeRunOutcomes
{
    public const string Unknown = "unknown";

    public const string Cancelled = "cancelled";

    /// <summary>
    ///     The <c>run_completed</c> statuses, which are the names of <c>AgentHomeGoalStatus</c>. A status outside this
    ///     set maps to <see cref="Unknown" />, which is what keeps a model-influenced log off the wire.
    /// </summary>
    public static readonly IReadOnlySet<string> CompletedStatuses =
        new HashSet<string>(StringComparer.Ordinal) { "NotRun", "Completed", "ToolCallBudgetExceeded", "TimeBudgetExceeded", "Failed" };
}

/// <summary>Whether a run's exported patch was landed on the host.</summary>
public static class AgentHomeRunApplyStates
{
    public const string None = "none";

    public const string Applied = "applied";

    public const string Rejected = "rejected";
}
