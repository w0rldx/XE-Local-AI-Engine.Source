namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Configuration for graph workflows.
///     <para>
///         <see cref="Enabled" /> gates <em>behaviour</em>, never registration — the same posture development
///         workflows and work sessions hold: a disabled node has to answer legibly rather than 500 out of an empty
///         container. The request-path gate in <c>Program</c> is what turns the switch into a 404.
///     </para>
///     <para>
///         The ranges here are sanity bounds the binder can see. The semantic floors and the one cross-option
///         relation live in <c>GraphWorkflowOptionsValidator</c>, which data annotations cannot express.
///     </para>
/// </summary>
public sealed class GraphWorkflowOptions
{
    public const string Section = "GraphWorkflows";

    public bool Enabled { get; init; }

    /// <summary>The cap on one definition's nodes, enforced when a definition is validated rather than when it runs.</summary>
    [Range(1, 10_000)]
    public int MaxNodesPerDefinition { get; init; } = 200;

    /// <summary>
    ///     The guard against a runaway fan-out. Never below <see cref="MaxNodesPerDefinition" />: a run that could not
    ///     instantiate the definition it started from would fail halfway through a graph the operator was allowed to save.
    /// </summary>
    [Range(1, 100_000)]
    public int MaxNodeRunsPerRun { get; init; } = 200;

    /// <summary>The guard against a retry storm: the outer bound on every attempt one run may spend, across all its nodes.</summary>
    [Range(1, 100_000)]
    public int MaxTotalAttempts { get; init; } = 50;

    /// <summary>How long a node run may take when its node names no timeout of its own.</summary>
    [Range(1, 86_400)]
    public int DefaultNodeTimeoutSeconds { get; init; } = 600;

    /// <summary>The cap on one node run's output document, checked before it is encrypted and stored.</summary>
    [Range(1, 64 * 1024 * 1024)]
    public int MaxOutputJsonBytes { get; init; } = 256 * 1024;

    /// <summary>
    ///     How often the dispatcher sweeps every live run, independently of the change signals it also listens for. A
    ///     dropped signal costs at most one interval of latency, never correctness. Floored at 100 ms by the validator:
    ///     below that the sweep spends more time opening scopes than advancing runs.
    /// </summary>
    [Range(1, 3_600_000)]
    public int DispatchIntervalMilliseconds { get; init; } = 500;

    /// <summary>How many runs may be live at once. Runs above the cap wait; they are not refused.</summary>
    [Range(1, 64)]
    public int MaxConcurrentRuns { get; init; } = 4;

    /// <summary>The cap on a run-start input document, checked at the endpoint so an oversized body never reaches the store.</summary>
    [Range(1, 64 * 1024 * 1024)]
    public int MaxRunInputBytes { get; init; } = 64 * 1024;

    /// <summary>
    ///     How many events one replay request may return. The hub ping is content-free, so a client that fell behind
    ///     re-reads through this window and is told when it was truncated rather than silently handed a partial log.
    /// </summary>
    [Range(1, 10_000)]
    public int EventReplayLimit { get; init; } = 200;
}
