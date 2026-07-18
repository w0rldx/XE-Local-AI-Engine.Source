namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using Microsoft.Extensions.DataIngestion;

/// <summary>
///     Splits a structured <see cref="IngestionDocument" /> into ordered sections and chunks for indexing. Pure and
///     deterministic: the same document always yields the same sections and chunks. Stateless and thread-safe.
/// </summary>
public interface IChunkingService
{
    /// <summary>
    ///     Walks the document's heading structure and produces the ordered section list plus size-bounded, overlapping
    ///     chunks. A document with no headers yields a single implicit section. Chunk size is token-aware: a section is cut
    ///     at whichever bound — a per-chunk token budget or the character ceiling — is reached first (always at a whitespace
    ///     boundary), so a chunk and its heading prefix stay within the embedding model's context window.
    /// </summary>
    /// <param name="document">The structured document to split.</param>
    /// <param name="embeddingContextWindowTokens">
    ///     The resolved embedding model's advertised context window in tokens, when discoverable at ingestion time. When
    ///     supplied and positive it TIGHTENS the per-chunk token budget (window minus a safety reserve) so a smaller-window
    ///     embedder yields correspondingly smaller chunks; it never enlarges chunks beyond the configured budget. When
    ///     <see langword="null" /> the configured <c>MaxChunkTokens</c> governs.
    /// </param>
    KnowledgeChunkingResult Chunk(IngestionDocument document, int? embeddingContextWindowTokens = null);
}
