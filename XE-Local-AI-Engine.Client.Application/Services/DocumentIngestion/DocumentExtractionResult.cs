namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     Outcome of extracting readable text from an uploaded document.
/// </summary>
/// <param name="Status">How the extraction resolved.</param>
/// <param name="Markdown">
///     The extracted Markdown/plaintext when <paramref name="Status"/> is
///     <see cref="DocumentExtractionStatus.Extracted"/>; <see langword="null"/> for every other status.
/// </param>
/// <param name="ExtractedChars">Length of <paramref name="Markdown"/> when extracted; otherwise <see langword="null"/>.</param>
/// <param name="Error">
///     A sanitized failure reason (never file content or the file name) when <paramref name="Status"/> is
///     <see cref="DocumentExtractionStatus.Failed"/>; otherwise <see langword="null"/>.
/// </param>
public sealed record DocumentExtractionResult(
    DocumentExtractionStatus Status,
    string? Markdown,
    int? ExtractedChars,
    string? Error);
