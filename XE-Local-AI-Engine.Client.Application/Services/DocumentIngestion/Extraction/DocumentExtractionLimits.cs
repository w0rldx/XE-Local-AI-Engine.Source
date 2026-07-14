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
    ///     Hard ceiling on the RAW upload bytes copied into the seekable buffer that feeds extraction (PdfPig and the
    ///     Open XML SDK both seek, so a forward-only upload stream must be materialized first). This caps the raw input
    ///     copy — not any decompressed or parser-expanded output — so it is simply a second bound on the in-memory buffer,
    ///     independent of the per-file upload cap. Copying past this ceiling fails extraction cleanly
    ///     (<see cref="DocumentExtractionStatus.Failed"/>) instead of risking an out-of-memory crash.
    ///     <para>
    ///         The per-file upload cap (<c>Security:MaxUploadFileSizeMb</c>, default 25 MiB, operator-configurable up to
    ///         512 MiB) is the primary bound and is stricter than this ceiling under the default, so this guard can never
    ///         fire out of the box. The headroom above the default is deliberate: this ceiling only bites when an operator
    ///         raises the upload cap above 200 MiB, keeping the extraction buffer bounded even then. Parser-internal
    ///         expansion is NOT bounded here — the reader still decompresses the container internally; that is caught after
    ///         materialization by the output char cap and the expansion-ratio guard.
    ///     </para>
    /// </summary>
    public const long MaxBufferedInputBytes = 200L * 1024 * 1024;

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
