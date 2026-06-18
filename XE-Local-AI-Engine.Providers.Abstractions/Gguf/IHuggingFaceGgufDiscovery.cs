namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Queries the Hugging Face Hub for GGUF repos and inspects their actual <c>.gguf</c> files. Repos with zero usable
///     GGUF files are excluded from search results. Public listing is anonymous; gated repos appear with
///     <see cref="GgufRepoSummary.IsGated" /> set and require a token only to <em>download</em> (the store's concern).
///     Lane C's advisor is the consumer.
/// </summary>
public interface IHuggingFaceGgufDiscovery
{
    /// <summary>Searches GGUF repos sorted by popularity; non-GGUF repos are filtered out.</summary>
    Task<IReadOnlyList<GgufRepoSummary>> SearchAsync(GgufSearchQuery query, CancellationToken ct);

    /// <summary>
    ///     Inspects one repo's actual <c>.gguf</c> files: per-file quant, byte size, integrity, and GGUF header metadata
    ///     read via HTTP range request (no full download). Unparseable files are skipped, not repo-dropping.
    /// </summary>
    Task<GgufRepoDetail> InspectRepoAsync(string repoId, CancellationToken ct);
}
