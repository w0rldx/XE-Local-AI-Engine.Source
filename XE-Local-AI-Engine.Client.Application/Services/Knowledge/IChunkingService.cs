namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using Microsoft.Extensions.DataIngestion;

/// <summary>
///     Splits a structured <see cref="IngestionDocument" /> into ordered sections and chunks for indexing. Pure and
///     deterministic: the same document always yields the same sections and chunks. Stateless and thread-safe.
/// </summary>
public interface IChunkingService
{
    /// <summary>
    ///     Walks the document's heading structure and produces the ordered section list plus size-bounded, overlapping chunks.
    /// </summary>
    /// <remarks>
    ///     A document with no headers yields a single implicit section. A section is cut at whichever bound is reached
    ///     first — the per-chunk token budget or the character ceiling — always at a whitespace boundary, so a chunk plus
    ///     its heading prefix stays inside the embedding model's context window. A supplied context window tightens the
    ///     token budget to that window minus a safety reserve, so a smaller-window embedder yields smaller chunks.
    /// </remarks>
    /// <param name="embeddingContextWindowTokens">
    ///     Resolved embedding context window in tokens; a positive value tightens the per-chunk token budget (never
    ///     enlarges it), <see langword="null" /> keeps the configured <c>MaxChunkTokens</c>.
    /// </param>
    KnowledgeChunkingResult Chunk(IngestionDocument document, int? embeddingContextWindowTokens = null);
}
