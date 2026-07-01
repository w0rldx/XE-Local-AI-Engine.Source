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
}
