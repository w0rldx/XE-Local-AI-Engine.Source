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
    ///     Grace period a run may spend with ZERO live hub subscribers before the sweeper cancels it. This is the ONLY
    ///     bound a Paused run is subject to: pause is deliberately exempt from <see cref="IdleTimeout" /> and
    ///     <see cref="MaxRunDuration" /> (a human may take arbitrarily long to press Continue), but "no human is
    ///     watching at all" is a different condition from "the human is thinking", and only the former may leak a
    ///     concurrency slot forever. A plain browser reload produces exactly that state: the run id lived only in the
    ///     old page, so nobody will ever Continue or Cancel it.
    ///     <para>
    ///         5 minutes is deliberately conservative — it must never kill a run whose only client is mid-reconnect.
    ///         A full page reload re-negotiates and re-subscribes within seconds; SignalR's automatic-reconnect
    ///         schedule gives up long before a minute; the longest legitimate gap is a suspended laptop. Five minutes
    ///         is an order of magnitude above every one of those, while still bounding a leaked slot to minutes rather
    ///         than to a node restart. A client that wants a longer window only has to stay subscribed.
    ///     </para>
    /// </summary>
    public TimeSpan AbandonedSubscriberGrace { get; set; } = TimeSpan.FromMinutes(5);

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

    /// <summary>
    ///     Backstop cap on the per-run replay buffer (the ordered event log a late subscriber replays on Subscribe).
    ///     Exceeding it drops the OLDEST buffered event so the log stays bounded. <see cref="MaxOutputBytes" /> already
    ///     bounds total output, so this only guards against a pathological event count.
    /// </summary>
    public int MaxBufferedEventsPerRun { get; set; } = 4096;

    /// <summary>
    ///     How long a run's event log lingers after its terminal event so a client that subscribes AFTER the run
    ///     finished can still replay and catch up. The sweeper evicts a terminal log once this elapses.
    /// </summary>
    public TimeSpan ReplayRetention { get; set; } = TimeSpan.FromSeconds(60);
}
