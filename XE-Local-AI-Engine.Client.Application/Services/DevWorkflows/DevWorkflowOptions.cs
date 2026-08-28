namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Configuration for development workflows.
///     <para>
///         <see cref="Enabled" /> gates <em>behaviour</em>, never registration — the same posture work sessions hold:
///         a disabled node has to answer legibly rather than 500 out of an empty container.
///     </para>
/// </summary>
public sealed class DevWorkflowOptions
{
    public const string Section = "DevWorkflows";

    public bool Enabled { get; init; }

    /// <summary>The cap on one workflow artifact's bytes, enforced by the blob store.</summary>
    [Range(1, 64 * 1024 * 1024)]
    public int MaxArtifactBytes { get; init; } = 1024 * 1024;

    /// <summary>
    ///     How often the dispatcher sweeps every live run, independently of the change signals it also listens for. A
    ///     dropped signal costs at most one interval of latency, never correctness — which is what lets the signal
    ///     channel drop writes rather than block a committing caller.
    /// </summary>
    [Range(1, 3600)]
    public int SweepSeconds { get; init; } = 5;

    /// <summary>
    ///     How many tool and dev-task node-runs may hold a sandbox at once. Two because a build is already multi-core;
    ///     the value exists because the attempt supervisor this lane copies has no cap at all, so a workflow fanning out
    ///     eight validation nodes would otherwise start eight builds.
    /// </summary>
    /// <remarks>ponytail: the default is a guess. Size it from real runs, not from another guess.</remarks>
    [Range(1, 32)]
    public int MaxParallelToolNodes { get; init; } = 2;

    /// <summary>
    ///     How often one node-run may resume its work session after the session parks on its own step budget. Parking is
    ///     routine — a workflow node needs more steps than one run allows — so this is a budget, not a failure count;
    ///     exhausting it blocks the node-run for a human rather than failing it.
    /// </summary>
    [Range(0, 100)]
    public int MaxSessionResumesPerNodeRun { get; init; } = 4;

    /// <summary>The guard against a runaway decomposition. Checked at materialization and again at startup recovery.</summary>
    [Range(1, 10_000)]
    public int MaxNodeRunsPerRun { get; init; } = 200;

    /// <summary>
    ///     The guard against a retry storm, and the outer bound on the cross-node fix loop: without it a definition
    ///     whose validation node re-attempts its implementation node could oscillate indefinitely.
    /// </summary>
    [Range(1, 100_000)]
    public int MaxTotalAttempts { get; init; } = 50;

    /// <summary>How many runs may be live at once. Runs above the cap wait; they are not refused.</summary>
    [Range(1, 64)]
    public int MaxConcurrentRuns { get; init; } = 4;
}
