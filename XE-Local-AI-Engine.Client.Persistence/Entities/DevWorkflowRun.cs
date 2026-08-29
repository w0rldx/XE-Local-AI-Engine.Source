namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevWorkflowRun
{
    public Guid Id { get; set; }
    public Guid WorkItemId { get; set; }
    public Guid DefinitionId { get; set; }
    public int DefinitionVersion { get; set; }
    public string DefinitionGraphHash { get; set; } = string.Empty;

    /// <summary>
    ///     The executed graph and the single source of routing truth: nodes, edges, conditions, join policies and
    ///     materialization templates. Pinned at start and rewritten in place when materialization expands it; the
    ///     definition row is never mutated, so re-running a definition is unaffected.
    /// </summary>
    public byte[] GraphJson { get; set; } = [];

    /// <summary>Zero at start, incremented per graph rewrite. The dispatcher's parsed-graph cache key is (run, revision).</summary>
    public int GraphRevision { get; set; }

    public DevWorkflowRunStatus Status { get; set; }

    /// <summary>
    ///     The run's single monotonic change watermark. Every node-run, event, artifact and decision insert takes a
    ///     fresh value from here inside the transaction that owns the run row, so one number answers "what changed
    ///     since?" across the whole subtree.
    /// </summary>
    public long LastSequence { get; set; }

    public string? FailureClass { get; set; }
    public string? TerminalReason { get; set; }
    public long? StartedAtUtc { get; set; }
    public long? EndedAtUtc { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
    public long Version { get; set; }
}
