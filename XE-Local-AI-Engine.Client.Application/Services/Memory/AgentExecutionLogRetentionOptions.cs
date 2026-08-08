namespace XE_Local_AI_Engine.Client.Services.Memory;

/// <summary>
///     Retention policy for the metadata-only <c>agent_execution_logs</c> telemetry table. The table is append-only (one
///     row per completed/failed run of a memory-enabled agent), so without a sweep it grows unbounded. The retention
///     background service deletes rows older than <see cref="RetentionDays" /> on a <see cref="SweepInterval" /> cadence,
///     and (when set) trims each agent to <see cref="MaxRowsPerAgent" /> newest rows.
/// </summary>
public sealed class AgentExecutionLogRetentionOptions
{
    public const string Section = "AgentExecutionLogRetention";

    /// <summary>Whether the retention sweep runs at all. When <c>false</c> the background service is a clean no-op.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Age threshold in days; rows whose <c>CreatedAtUtc</c> is older than now minus this are deleted.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>How often the sweep runs.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    ///     Optional per-agent row cap applied after the time-based sweep — each agent keeps this many newest rows and the
    ///     rest are deleted. <c>null</c> (default) disables the cap; only the time-based sweep runs.
    /// </summary>
    public int? MaxRowsPerAgent { get; set; }
}
