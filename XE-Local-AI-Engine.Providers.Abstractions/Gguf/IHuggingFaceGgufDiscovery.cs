namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Queries the Hugging Face Hub for GGUF repos and inspects their actual <c>.gguf</c> files. Repos with zero usable
///     GGUF files are excluded from search results. Public listing is anonymous; gated repos appear with
///     <see cref="GgufRepoSummary.IsGated" /> set and require a token only to <em>download</em> (the store's concern).
///     The model-fit advisor is the consumer.
/// </summary>
public interface IHuggingFaceGgufDiscovery
{
    /// <summary>Searches GGUF repos in the requested order (trending by default); non-GGUF repos are filtered out.</summary>
    Task<IReadOnlyList<GgufRepoSummary>> SearchAsync(GgufSearchQuery query, CancellationToken ct);

    /// <summary>
    ///     Inspects one repo's actual <c>.gguf</c> files: per-file quant, byte size, integrity, and GGUF header metadata
    ///     read via HTTP range request (no full download). Unparseable files and <c>mmproj</c> projector companions are
    ///     skipped, not repo-dropping.
    /// </summary>
    Task<GgufRepoDetail> InspectRepoAsync(string repoId, CancellationToken ct);

    /// <summary>
    ///     Lists one repo's selectable <c>.gguf</c> files (per-file quant + byte size + integrity) WITHOUT the per-file
    ///     GGUF header range-read — a lighter, faster inspection for interactive surfaces (the download quant picker)
    ///     that do not need the header metadata. <c>mmproj</c> projector companions are excluded. The header fields on
    ///     each returned <see cref="GgufRepoFile" /> are <see langword="null" />.
    /// </summary>
    Task<GgufRepoDetail> ListRepoFilesAsync(string repoId, CancellationToken ct);

    /// <summary>
    ///     Finds the repo's multimodal projector (<c>mmproj</c>) companion — the vision encoder a vision GGUF needs — or
    ///     <see langword="null" /> when the repo ships none (a text-only model). Unlike the selectable-file listings above
    ///     (which exclude projectors), this surfaces the projector so the store can download it alongside the chosen
    ///     quant. When a repo ships several projector precisions, the highest-precision one is returned.
    /// </summary>
    Task<GgufProjectorFile?> FindProjectorAsync(string repoId, CancellationToken ct);
}
