namespace XE_Local_AI_Engine.Client.Services.Memory;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Semantic (embedding-cosine) dedup layer for extracted memory candidates. It runs ON TOP OF the always-on lexical
///     dedup in <c>MemoryExtractionService</c>: given the candidates that already survived the lexical (normalized-text)
///     pass, it embeds each candidate's behaviour with the node-local embedding model and flags any that are cosine-near
///     an existing live memory of the SAME scope (a paraphrase the exact lexical key misses). The lexical pass stays the
///     fast/robust baseline; this only catches near-duplicates it cannot.
///     The layer is gated on <c>EmbeddingModelResolution.IsConfident</c>: with no confident node-local embedding model
///     (provider unreachable, or nothing installed matched) — or any embedding failure — it returns
///     <see cref="MemorySemanticDedupResult.NotApplied" /> so the caller keeps its lexical-only result. A transient
///     provider outage therefore NEVER mass-dedups (silently swallows) legitimate new candidates. Candidate/memory text
///     never leaves the node; existing-memory vectors are held in a RAM-only, bounded cache keyed by
///     (id, version, resolved-model-name) and are never persisted or logged; the query candidate is re-embedded per run.
/// </summary>
internal interface IMemorySemanticDeduplicator
{
    /// <summary>
    ///     Flags which of <paramref name="candidates" /> are semantic duplicates of an existing live memory (same scope)
    ///     or of an earlier accepted candidate in this same batch. The returned indexes are positions into
    ///     <paramref name="candidates" />. When semantic dedup does not run (disabled, no confident embedding model, or an
    ///     embedding failure) the result is <see cref="MemorySemanticDedupResult.NotApplied" /> and the caller must keep
    ///     every candidate (lexical-only fallback).
    /// </summary>
    Task<MemorySemanticDedupResult> FindSemanticDuplicatesAsync(IReadOnlyList<MemoryDedupExisting> existing,
        IReadOnlyList<MemoryDedupCandidate> candidates,
        CancellationToken cancellationToken);
}

/// <summary>An existing live (Suggested/Enabled) memory to compare candidates against. <see cref="Id" />/
///     <see cref="Version" /> key its RAM-only cached embedding; <see cref="Scope" /> confines the comparison to the same
///     scope; <see cref="Behavior" /> is the text embedded.</summary>
internal sealed record MemoryDedupExisting(Guid Id, int Version, MemoryScope Scope, string Behavior);

/// <summary>A lexically-surviving candidate to test for semantic duplication. <see cref="Behavior" /> is the (already
///     secret-redacted) text embedded; <see cref="Scope" /> confines the comparison to same-scope memories.</summary>
internal sealed record MemoryDedupCandidate(MemoryScope Scope, string Behavior);

/// <summary>
///     Outcome of one <see cref="IMemorySemanticDeduplicator.FindSemanticDuplicatesAsync" /> call.
///     <see cref="Applied" /> is <c>true</c> only when semantic dedup actually ran (a confident embedding model produced
///     comparable vectors); <see cref="DuplicateIndexes" /> then holds the candidate positions to drop. When
///     <see cref="Applied" /> is <c>false</c> the caller keeps every candidate — the lexical-only fallback that guarantees
///     no candidate is dropped during a provider outage.
/// </summary>
internal sealed record MemorySemanticDedupResult(bool Applied, IReadOnlySet<int> DuplicateIndexes)
{
    /// <summary>The "did not run" result: no candidate is a semantic duplicate; the caller keeps them all.</summary>
    public static MemorySemanticDedupResult NotApplied { get; } = new(Applied: false, new HashSet<int>());
}
