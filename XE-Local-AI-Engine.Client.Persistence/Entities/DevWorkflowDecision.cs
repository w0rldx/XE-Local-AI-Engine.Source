namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A human unblocking a node-run — a gate approval or a retries-exhausted intervention. One node-run legitimately
///     accumulates several of these over its life (fail, <c>Retry</c> at attempt 1, <c>Approve</c> at attempt 2), which
///     is why the uniqueness is per <c>(node run, attempt)</c> and not per node-run.
/// </summary>
internal sealed class DevWorkflowDecision
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid NodeRunId { get; set; }
    public int Attempt { get; set; }
    public DevWorkflowDecisionKind Decision { get; set; }
    public byte[]? Comment { get; set; }

    /// <summary>The structured, machine-consumed half of a decision. A distinct AAD column name from the free-text comment so neither is substitutable for the other.</summary>
    public byte[]? PayloadJson { get; set; }

    public string? DecidedBySubject { get; set; }
    public Guid OperationId { get; set; }
    public long Sequence { get; set; }
    public long DecidedAtUtc { get; set; }
}
