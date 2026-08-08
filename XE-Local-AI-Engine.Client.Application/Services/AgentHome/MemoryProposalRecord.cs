namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     One validated memory proposal from the agent's JSONL output. All string values
///     are already secret-scanned; content may contain <c>[REDACTED:&lt;class&gt;]</c> placeholders where the scanner
///     replaced a secret match (the record is still useful). Records rejected outright are captured as
///     <see cref="MemoryProposalRejection" /> on the collect result instead.
/// </summary>
internal sealed record MemoryProposalRecord
{
    /// <summary>
    ///     Proposal target: <c>node_memory_proposal</c> or <c>project_memory_proposal</c>.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>The operation: <c>add</c>, <c>update</c>, or <c>remove</c>.</summary>
    public required string Operation { get; init; }

    /// <summary>
    ///     The proposal content (1–4000 characters; may contain <c>[REDACTED:&lt;class&gt;]</c> placeholders after
    ///     secret scanning).
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    ///     Evidence paths (sandbox-relative, starting with <c>/agent-home/workspace/selected/</c>). Empty list is
    ///     valid.
    /// </summary>
    public required IReadOnlyList<string> Evidence { get; init; }

    /// <summary>The agent-assessed confidence: <c>low</c>, <c>medium</c>, or <c>high</c>.</summary>
    public required string Confidence { get; init; }

    /// <summary>
    ///     Zero-based index of the source line within its JSONL file; carried for diagnostics so the caller can
    ///     correlate rejections and accepted proposals back to the raw artifact.
    /// </summary>
    public required int SourceLineIndex { get; init; }

    /// <summary>
    ///     The JSONL file name (e.g. <c>node-memory.proposals.jsonl</c>) this record was read from, relative to the
    ///     proposals directory. Never a full host path.
    /// </summary>
    public required string SourceFileName { get; init; }
}

/// <summary>
///     A proposal record that was rejected by the validator or secret scanner, together with the reason. The raw line
///     is intentionally omitted from the model-facing surface to avoid re-exposing secrets.
/// </summary>
internal sealed record MemoryProposalRejection
{
    /// <summary>Zero-based line index within its JSONL file.</summary>
    public required int SourceLineIndex { get; init; }

    /// <summary>The JSONL file name the rejected record was read from.</summary>
    public required string SourceFileName { get; init; }

    /// <summary>Human-readable reason for the rejection (safe to log; must not contain the raw secret value).</summary>
    public required string Reason { get; init; }
}
