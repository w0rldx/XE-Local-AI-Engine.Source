namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Memory-proposal export: collects the agent-written JSONL proposal files from a run's
///     <c>/agent-home/runs/&lt;run-id&gt;/memory/proposals/</c> directory.
/// </summary>
/// <remarks>
///     Each record is validated against the proposal schema and passed through a regex-based secret scan before it
///     is returned. The service never mutates real node or platform memory: proposals are durable run artifacts
///     returned to the caller for later user/platform review.
/// </remarks>
internal interface IAgentHomeMemoryProposalService
{
    /// <summary>
    ///     Reads JSONL proposal files from the run's host-side memory directory, validates each record, applies the
    ///     proposal secret scan, and returns the surviving proposals.
    /// </summary>
    /// <remarks>
    ///     Malformed records and records carrying secrets in non-content fields are rejected and logged, while
    ///     content-only secret matches are redacted. A bad record never throws: validation and scan errors are
    ///     surfaced as <see cref="MemoryProposalRejection" /> entries on the result.
    /// </remarks>
    Task<MemoryProposalCollectResult> CollectAsync(MemoryProposalCollectRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Inputs for <see cref="IAgentHomeMemoryProposalService.CollectAsync" />.</summary>
internal sealed record MemoryProposalCollectRequest
{
    /// <summary>The run id; proposal files live under <c>runs/&lt;run-id&gt;/memory/proposals/</c>.</summary>
    public required string RunId { get; init; }

    /// <summary>
    ///     The worker-local host run directory (<c>&lt;RootPath&gt;/runs/&lt;run-id&gt;</c>); the collector reads the
    ///     <c>memory/proposals/</c> subdirectory from here. This is the host root, not the in-sandbox
    ///     <c>/agent-home</c>.
    /// </summary>
    public required string HostRunDirectory { get; init; }
}

/// <summary>Outcome of <see cref="IAgentHomeMemoryProposalService.CollectAsync" />.</summary>
internal sealed record MemoryProposalCollectResult
{
    /// <summary>Validated and secret-scanned proposals ready for user/platform review.</summary>
    public required IReadOnlyList<MemoryProposalRecord> Proposals { get; init; }

    /// <summary>Records that were rejected due to schema violations or unredactable secrets.</summary>
    public required IReadOnlyList<MemoryProposalRejection> Rejections { get; init; }
}
