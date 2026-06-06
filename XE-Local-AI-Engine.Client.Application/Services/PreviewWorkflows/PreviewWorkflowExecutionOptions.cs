namespace XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>
///     Tunables for the in-memory preview run registry. Defaults are deliberately conservative — preview runs are
///     one-shot, in-memory, operator-driven, and never persisted, so the caps exist to bound a stuck/abandoned run, not
///     to throttle throughput.
/// </summary>
public sealed class PreviewWorkflowExecutionOptions
{
    public const string Section = "PreviewWorkflows:Execution";

    /// <summary>
    ///     Idle TTL: a run is swept (cancelled + disposed) when this long passes with no activity. The clock is
    ///     SUSPENDED while a run is Paused (a paused run waits on a human Continue and must not be swept — findings
    ///     item 6 / <c>OrchestrationRunSession</c>'s <c>Timeout.InfiniteTimeSpan</c> pattern).
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Hard wall-clock cap on a Running run regardless of activity, so a reload that fails to disconnect cannot burn
    ///     compute forever. A Paused run is exempt from THIS cap (it is gated by Continue/cancel/disconnect instead).
    /// </summary>
    public TimeSpan MaxRunDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How often the sweeper checks for idle / over-cap runs.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Maximum concurrent runs in the registry. A new run beyond this returns a <c>CapReached</c> 409. Default 4
    ///     keeps node CPU/RAM bounded — preview is a single-operator debugging tool, not a batch executor.
    /// </summary>
    public int MaxConcurrentRuns { get; set; } = 4;

    /// <summary>
    ///     Per-run accumulated output byte cap. Exceeding it cancels the run with <c>preview.run.failed</c>
    ///     ("output limit exceeded"). Mirrors <c>InvocationRunner</c>'s 10&#160;MB default (<c>MaxResponseSizeMb</c>).
    /// </summary>
    public int MaxOutputBytes { get; set; } = 10 * 1024 * 1024;
}
