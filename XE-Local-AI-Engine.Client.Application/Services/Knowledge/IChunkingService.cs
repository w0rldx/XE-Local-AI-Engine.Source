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
    ///     chunks. A document with no headers yields a single implicit section.
    /// </summary>
    KnowledgeChunkingResult Chunk(IngestionDocument document);
}
