namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One row per <c>(run, node key)</c>. <see cref="Attempt" /> increments in place; there is no per-attempt row.
///     Per-attempt history lives in the run event log, which is what makes the <c>(run_id, node_key)</c> unique index
///     the node run's identity rather than a secondary constraint.
/// </summary>
internal sealed class GraphWorkflowNodeRun
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }

    /// <summary>The graph node key. Structural, so plaintext — labels and instructions stay in the encrypted graph.</summary>
    public string NodeKey { get; set; } = string.Empty;

    public GraphWorkflowNodeKind Kind { get; set; }
    public GraphWorkflowNodeRunStatus Status { get; set; }
    public int Attempt { get; set; }

    /// <summary>What human answer this node run is blocked on, so a run summary counts pending decisions without a run-level column.</summary>
    public GraphWorkflowDecisionKind? PendingDecisionKind { get; set; }

    /// <summary>The decide endpoint's idempotency key, unique per run among the rows that carry one.</summary>
    public Guid? DecisionOperationId { get; set; }

    /// <summary>Who answered the pause. Encrypted: a subject identifier is not structural.</summary>
    public byte[]? DecidedBySubject { get; set; }

    public GraphWorkflowFailureClass FailureClass { get; set; }

    /// <summary>The failure text. Encrypted — a provider error can quote the payload that produced it.</summary>
    public byte[]? Error { get; set; }

    /// <summary>The resolved input document handed to the executor.</summary>
    public byte[]? InputJson { get; set; }

    /// <summary>The node's output document. Edge conditions route on this, which is why it carries its own AAD column name.</summary>
    public byte[]? OutputJson { get; set; }

    /// <summary>The headless invocation an Agent node owns. Loose reference, no foreign key.</summary>
    public Guid? InvocationId { get; set; }

    public long? StartedAtUtc { get; set; }
    public long? CompletedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}
