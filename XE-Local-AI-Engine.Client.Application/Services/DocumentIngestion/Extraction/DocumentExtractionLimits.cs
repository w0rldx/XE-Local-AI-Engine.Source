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
    ///     Hard ceiling on the RAW upload bytes copied into the seekable buffer that feeds extraction.
    /// </summary>
    /// <remarks>
    ///     PdfPig and the Open XML SDK both seek, so a forward-only upload stream must be materialized first. This caps the
    ///     raw input copy, not decompressed output: a second bound on the in-memory buffer, independent of the per-file
    ///     upload cap (<c>Security:MaxUploadFileSizeMb</c>, default 25 MiB, up to 512 MiB), which is the primary and
    ///     stricter bound by default, so this only bites above a 200 MiB cap. Copying past it fails extraction cleanly
    ///     instead of risking an OOM crash; parser-internal expansion is caught later by the char cap and ratio guard.
    /// </remarks>
    public const long MaxBufferedInputBytes = 200L * 1024 * 1024;

    /// <summary>
    ///     Upper bound on the total characters a STRUCTURED extraction (<c>ExtractStructuredAsync</c>) may yield.
    /// </summary>
    /// <remarks>
    ///     Unlike the conversation path, the structured document is returned verbatim because the chunker needs its
    ///     heading structure, so nothing else caps its aggregate size before chunking and persistence. Exceeding this
    ///     fails extraction cleanly. It is deliberately larger than <see cref="DefaultMaxOutputChars" />, since chunking
    ///     bounds each chunk separately, so only a pathologically large document is rejected.
    /// </remarks>
    public const int DefaultMaxStructuredOutputChars = 20_000_000;

    /// <summary>
    ///     Expansion-ratio ceiling: the ratio of extracted characters to the bytes read from the upload.
    /// </summary>
    /// <remarks>
    ///     A parser can inflate a small container far beyond the absolute char cap's expectation — a tiny zip or pdf
    ///     expanding into an enormous text body — which the absolute cap alone would still persist while it stayed under
    ///     the ceiling. Output whose char count exceeds <c>inputBytes * ratio</c>, and clears
    ///     <see cref="MinCharsForExpansionGuard" /> so ordinary small files are never flagged, fails extraction cleanly.
    /// </remarks>
    public const int DefaultMaxExpansionRatio = 200;

    /// <summary>Floor below which the expansion-ratio guard never fires.</summary>
    /// <remarks>
    ///     A small file legitimately produces a small, high-ratio output — a 20-byte note rendering to 40 chars is a 2x
    ///     ratio that means nothing — so the ratio guard is only meaningful once the absolute output is already large.
    /// </remarks>
    public const int MinCharsForExpansionGuard = 1_000_000;

    /// <summary>Maximum synchronous in-request conversation extractions admitted at once.</summary>
    /// <remarks>
    ///     Each extraction buffers the whole upload, up to the per-file cap, in memory, so unbounded concurrent uploads
    ///     could aggregate to an out-of-memory condition even though each single file is within its cap. The gate admits
    ///     this many and rejects the rest with a busy status. Knowledge-base extraction runs in the already-bounded
    ///     background worker and is not gated here.
    /// </remarks>
    public const int DefaultMaxConcurrentExtractions = 4;

    /// <summary>
    ///     Pre-parse ceiling on the number of entries a ZIP-based container such as <c>.docx</c> may declare.
    /// </summary>
    /// <remarks>
    ///     Read from the archive's End Of Central Directory record — and the Zip64 EOCD when the classic count field is
    ///     saturated — BEFORE any <see cref="System.IO.Compression.ZipArchive" /> is constructed, so an entry-count zip
    ///     bomb is rejected while allocating ZERO entry objects: reading <c>ZipArchive.Entries</c> materializes the WHOLE
    ///     central directory up front. Only once the declared count clears this ceiling is the archive opened for the
    ///     size/ratio pass, which materializes at most that many entry METADATA, never decompressed bytes.
    /// </remarks>
    public const int DefaultMaxCompressedEntryCount = 10_000;

    /// <summary>
    ///     Pre-parse ceiling on the SUM of the declared uncompressed lengths of a ZIP container's entries.
    /// </summary>
    /// <remarks>
    ///     Read as <c>ZipArchiveEntry.Length</c> from the central directory, with no decompression, this bounds the memory
    ///     the parser would allocate expanding a small compressed container into a huge one. Deliberately generous at
    ///     512 MiB: no realistic Office document is rejected, while the true bomb shape sits far above it. A hostile
    ///     archive can LIE in its central directory, so the post-parse output-char cap and expansion-ratio guard in
    ///     <see cref="DocumentTextExtractor" /> remain the backstop measuring the ACTUAL expanded output.
    /// </remarks>
    public const long DefaultMaxDeclaredUncompressedBytes = 512L * 1024 * 1024;

    /// <summary>Pre-parse ceiling on a ZIP container's declared expansion ratio.</summary>
    /// <remarks>
    ///     That ratio is the SUM of declared uncompressed entry lengths divided by the SUM of their compressed lengths,
    ///     both read from the central directory. A classic zip bomb declares a ratio in the thousands and ordinary Office
    ///     XML compresses well under 20x, so this generous 200x ceiling — matching
    ///     <see cref="DefaultMaxExpansionRatio" /> — flags only pathological archives. As with
    ///     <see cref="DefaultMaxDeclaredUncompressedBytes" />, a lying central directory is caught by the post-parse guards.
    /// </remarks>
    public const int DefaultMaxCompressionRatio = 200;

    /// <summary>Pre-parse ceiling on the page count a PDF declares.</summary>
    /// <remarks>
    ///     PdfPig exposes <c>NumberOfPages</c> as soon as the document is opened, reading the cross-reference and catalog
    ///     rather than the per-page content streams, so an outrageous declared count is rejected before the expensive text
    ///     extraction. Deliberately generous: a large legitimate book runs a few thousand pages. PDF preflight is limited
    ///     to this cheap signal, so the post-parse char cap and ratio guard remain the backstop for a PDF that declares
    ///     few pages but expands each into an enormous text body.
    /// </remarks>
    public const int DefaultMaxPdfPageCount = 10_000;
}
