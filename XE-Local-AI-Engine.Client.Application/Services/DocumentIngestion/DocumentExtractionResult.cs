namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     Outcome of extracting readable text from an uploaded document.
/// </summary>
public sealed class DocumentExtractionResult
{
    /// <summary>How the extraction resolved.</summary>
    public required DocumentExtractionStatus Status { get; init; }

    /// <summary>
    ///     The extracted Markdown/plaintext when <see cref="Status" /> is
    ///     <see cref="DocumentExtractionStatus.Extracted"/>; <see langword="null"/> for every other status.
    /// </summary>
    public required string? Markdown { get; init; }

    /// <summary>Length of <see cref="Markdown" /> when extracted; otherwise <see langword="null"/>.</summary>
    public required int? ExtractedChars { get; init; }

    /// <summary>
    ///     A sanitized failure reason (never file content or the file name) when <see cref="Status" /> is
    ///     <see cref="DocumentExtractionStatus.Failed"/>; otherwise <see langword="null"/>.
    /// </summary>
    public required string? Error { get; init; }
}
