namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion.Extraction;

/// <summary>
///     Shared bounds for document extraction.
/// </summary>
internal static class DocumentExtractionLimits
{
    /// <summary>
    ///     Upper bound on the number of extracted characters. Output longer than this is truncated to bound memory
    ///     for very large documents; the extraction still resolves as
    ///     <see cref="DocumentExtractionStatus.Extracted"/>.
    /// </summary>
    public const int DefaultMaxOutputChars = 5_000_000;

    /// <summary>
    ///     Ceiling on the bytes materialized into memory while buffering an upload for extraction. A container format
    ///     such as a .docx (zip) or a .pdf can expand well beyond its on-disk size, so a hostile "decompression bomb"
    ///     upload could otherwise exhaust memory before the char-count truncation runs. Buffering past this ceiling fails
    ///     extraction cleanly (<see cref="DocumentExtractionStatus.Failed"/>) instead of risking an out-of-memory crash.
    /// </summary>
    public const long MaxDecompressedBytes = 200L * 1024 * 1024;

    /// <summary>
    ///     Upper bound on the total characters a STRUCTURED extraction (<c>ExtractStructuredAsync</c>) may yield. Unlike
    ///     the conversation path, the structured document is returned verbatim (the chunker needs its heading structure),
    ///     so nothing capped its aggregate size before it reached chunking/persistence. Exceeding this fails extraction
    ///     cleanly. Deliberately generous — larger than <see cref="DefaultMaxOutputChars"/> because chunking bounds each
    ///     chunk separately — so only a pathologically large document is rejected.
    /// </summary>
    public const int DefaultMaxStructuredOutputChars = 20_000_000;

    /// <summary>
    ///     Expansion-ratio ceiling: the ratio of extracted characters to the bytes read from the upload. A parser can
    ///     inflate a small container far beyond the absolute char cap's expectation (a tiny zip/pdf expanding into an
    ///     enormous text body), which the absolute cap alone would still persist as long as it stayed under the ceiling.
    ///     Output whose char count exceeds <c>inputBytes * ratio</c> — and clears
    ///     <see cref="MinCharsForExpansionGuard"/> so ordinary small files are never flagged — fails extraction cleanly.
    /// </summary>
    public const int DefaultMaxExpansionRatio = 200;

    /// <summary>
    ///     Floor below which the expansion-ratio guard never fires. A small file legitimately produces a small,
    ///     high-ratio output (a 20-byte note rendering to 40 chars is a 2x ratio that means nothing); the ratio guard is
    ///     only meaningful once the absolute output is already large.
    /// </summary>
    public const int MinCharsForExpansionGuard = 1_000_000;

    /// <summary>
    ///     Maximum synchronous in-request conversation extractions admitted at once. Each extraction buffers the whole
    ///     upload (up to the per-file cap) in memory, so unbounded concurrent uploads could aggregate to an
    ///     out-of-memory condition even though each single file is within its cap. The gate admits this many and rejects
    ///     the rest with a busy status. Knowledge-base extraction runs in the background worker (already bounded) and is
    ///     not gated here.
    /// </summary>
    public const int DefaultMaxConcurrentExtractions = 4;
}
