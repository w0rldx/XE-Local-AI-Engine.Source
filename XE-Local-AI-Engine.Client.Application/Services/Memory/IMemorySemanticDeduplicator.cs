namespace XE_Local_AI_Engine.Client.Services.Memory;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Semantic (embedding-cosine) dedup for extracted memory candidates, running ON TOP OF the always-on lexical
///     dedup to catch the paraphrases an exact lexical key misses.
/// </summary>
/// <remarks>
///     Each candidate that survived the lexical pass is embedded with the node-local model and flagged when it is
///     cosine-near a live memory of the SAME scope. It is gated on <c>EmbeddingModelResolution.IsConfident</c>: no
///     confident model, or any embedding failure, returns <see cref="MemorySemanticDedupResult.NotApplied" /> so the
///     caller keeps its lexical-only result and a transient outage never mass-dedups legitimate candidates. Text
///     never leaves the node, and memory vectors live in a RAM-only bounded cache that is never persisted or logged.
/// </remarks>
internal interface IMemorySemanticDeduplicator
{
    /// <summary>
    ///     Flags which of <paramref name="candidates" /> duplicate a live memory of the same scope, or an earlier
    ///     accepted candidate in this batch, as positions into <paramref name="candidates" />.
    /// </summary>
    /// <remarks>
    ///     When semantic dedup does not run — disabled, no confident embedding model, or an embedding failure — the
    ///     result is <see cref="MemorySemanticDedupResult.NotApplied" /> and the caller keeps every candidate.
    /// </remarks>
    Task<MemorySemanticDedupResult> FindSemanticDuplicatesAsync(IReadOnlyList<MemoryDedupExisting> existing,
        IReadOnlyList<MemoryDedupCandidate> candidates,
        CancellationToken cancellationToken);
}

/// <summary>An existing live (Suggested/Enabled) memory to compare candidates against. <see cref="Id" />/
///     <see cref="Version" /> key its RAM-only cached embedding; <see cref="Scope" /> confines the comparison to the same
///     scope; <see cref="Behavior" /> is the text embedded.</summary>
internal sealed class MemoryDedupExisting
{
    public required Guid Id { get; init; }

    public required int Version { get; init; }

    public required MemoryScope Scope { get; init; }

    public required string Behavior { get; init; }
}

/// <summary>A lexically-surviving candidate to test for semantic duplication. <see cref="Behavior" /> is the (already
///     secret-redacted) text embedded; <see cref="Scope" /> confines the comparison to same-scope memories.</summary>
internal sealed class MemoryDedupCandidate
{
    public required MemoryScope Scope { get; init; }

    public required string Behavior { get; init; }
}

/// <summary>
///     Outcome of one <see cref="IMemorySemanticDeduplicator.FindSemanticDuplicatesAsync" /> call.
/// </summary>
/// <remarks>
///     <see cref="Applied" /> is <c>true</c> only when semantic dedup actually ran, and
///     <see cref="DuplicateIndexes" /> then holds the positions to drop. When it is <c>false</c> the caller keeps
///     every candidate, the lexical-only fallback that drops nothing during a provider outage.
/// </remarks>
internal sealed class MemorySemanticDedupResult
{
    public required bool Applied { get; init; }

    public required IReadOnlySet<int> DuplicateIndexes { get; init; }

    /// <summary>The "did not run" result: no candidate is a semantic duplicate; the caller keeps them all.</summary>
    public static MemorySemanticDedupResult NotApplied { get; } = new() { Applied = false, DuplicateIndexes = new HashSet<int>() };
}
