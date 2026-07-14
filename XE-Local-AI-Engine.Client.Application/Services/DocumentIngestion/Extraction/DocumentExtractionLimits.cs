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

    /// <summary>
    ///     Pre-parse ceiling on the number of entries a ZIP-based container (e.g. <c>.docx</c>) may declare in its
    ///     central directory. Read cheaply from the central directory BEFORE the parser decompresses anything, so an
    ///     archive padded with an absurd number of members (an entry-count zip bomb) is rejected without materializing
    ///     any of them. A legitimate Office document has at most a few dozen parts, so this is deliberately generous.
    /// </summary>
    public const int DefaultMaxCompressedEntryCount = 10_000;

    /// <summary>
    ///     Pre-parse ceiling on the SUM of the declared uncompressed lengths of a ZIP container's entries
    ///     (<c>ZipArchiveEntry.Length</c>, read from the central directory — no decompression). Rejecting here bounds the
    ///     memory the parser would otherwise allocate expanding a small compressed container into a huge one. Deliberately
    ///     generous (512 MiB) so no realistic Office document is rejected; the true bomb shape (tens of MB compressed
    ///     declaring gigabytes uncompressed) sits far above it.
    ///     <para>
    ///         A hostile archive can LIE in its central directory (declare a small size, then expand hugely), so this
    ///         header-trusting check is NOT the only guard: the post-parse output-char cap and expansion-ratio guard in
    ///         <see cref="DocumentTextExtractor"/> remain the backstop that measures the ACTUAL expanded output. The two
    ///         layers together are the defense in depth — cheap honest-header rejection up front, real-output rejection
    ///         behind it.
    ///     </para>
    /// </summary>
    public const long DefaultMaxDeclaredUncompressedBytes = 512L * 1024 * 1024;

    /// <summary>
    ///     Pre-parse ceiling on a ZIP container's declared expansion ratio: the SUM of declared uncompressed entry
    ///     lengths divided by the SUM of their compressed lengths, both read from the central directory. A classic zip
    ///     bomb declares a ratio in the thousands; ordinary Office XML compresses well under 20x, so this generous 200x
    ///     ceiling (matching <see cref="DefaultMaxExpansionRatio"/>) flags only pathological archives. As with
    ///     <see cref="DefaultMaxDeclaredUncompressedBytes"/>, a lying central directory is caught behind this by the
    ///     post-parse guards.
    /// </summary>
    public const int DefaultMaxCompressionRatio = 200;

    /// <summary>
    ///     Pre-parse ceiling on the page count a PDF declares. PdfPig exposes <c>NumberOfPages</c> as soon as the
    ///     document is opened (it reads the cross-reference/catalog, not the per-page content streams), so a PDF that
    ///     declares an outrageous page count is rejected before its pages' text is extracted — the expensive step.
    ///     Deliberately generous: a large legitimate book runs a few thousand pages, so 10_000 rejects only abusive
    ///     inputs. PDF preflight is limited to this cheap page-count signal the library exposes; the post-parse output
    ///     char cap and expansion-ratio guard remain the backstop for a PDF that declares few pages but expands each into
    ///     an enormous text body.
    /// </summary>
    public const int DefaultMaxPdfPageCount = 10_000;
}
