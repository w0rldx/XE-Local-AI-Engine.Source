namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using XE_Local_AI_Engine.Client.Services.DocumentIngestion.Implementation;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;

/// <summary>Maps a persisted knowledge-document failure reason to a stable code the UI translates.</summary>
/// <remarks>
///     The stored reason stays the fixed English text other readers (MCP tools, logs) already use; the code is derived
///     on read, so existing rows translate too. A reason with no code (an extractor limit that names a number) is shown
///     as stored.
/// </remarks>
public static class KnowledgeFailureReasonCodes
{
    // Older rows carry the reader's exception type: "Extraction failed (<exception type>)."
    private const string LegacyExtractionFailedPrefix = "Extraction failed (";

    private static readonly Dictionary<string, string> CodesByReason = new(StringComparer.Ordinal)
    {
        [KnowledgeIngestionService.ContentMissingReason] = "content-missing",
        [KnowledgeIngestionService.UnsupportedReason] = "unsupported-file-type",
        [KnowledgeIngestionService.ExtractionFailedReason] = "extraction-failed",
        [DocumentTextExtractor.UnreadableReason] = "unreadable-file",
        [DocumentTextExtractor.PdfUnparseableReason] = "unreadable-file",
        [KnowledgeIngestionService.EmptyDocumentReason] = "no-text",
        [KnowledgeIngestionService.UnexpectedReason] = "unexpected",
        [KnowledgeIngestionService.EmbeddingModelChangedReason] = "embedding-model-changed",
        [KnowledgeIngestionService.TimedOutReason] = "timed-out",
        [KnowledgeChunkEmbedder.EmbeddingUnavailableReason] = "embedding-unavailable",
        [KnowledgeChunkEmbedder.EmbeddingIncompleteReason] = "embedding-incomplete",
        [KnowledgeChunkEmbedder.EmbeddingRejectedReason] = "embedding-rejected",
        [KnowledgeChunkEmbedder.EmbeddingUnreachableReason] = "embedding-unreachable",
        [KnowledgeEmbeddingVectorPolicy.InvalidVectorReason] = "invalid-vector",
        [KnowledgeEmbeddingVectorPolicy.ShortVectorReason] = "short-vector",
        [KnowledgeDocumentCatalogService.InterruptedRepeatedlyReason] = "interrupted-repeatedly"
    };

    /// <summary>The code for <paramref name="reason" />, or <see langword="null" /> when it has none.</summary>
    public static string? CodeOf(string? reason)
    {
        if (reason is null)
        {
            return null;
        }

        if (CodesByReason.TryGetValue(reason, out var code))
        {
            return code;
        }

        return reason.StartsWith(LegacyExtractionFailedPrefix, StringComparison.Ordinal) ? "unreadable-file" : null;
    }
}
