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
}
