namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

using Microsoft.Extensions.DataIngestion;

/// <summary>
///     Outcome of extracting the structured <see cref="IngestionDocument" /> (sections + headers + paragraphs) from an
///     uploaded document, before it is flattened to Markdown. Mirrors <see cref="DocumentExtractionResult" /> but carries
///     the reader's structured document instead of a Markdown string, so the chunking lane can walk the heading structure.
/// </summary>
/// <param name="Status">How the extraction resolved.</param>
/// <param name="Document">
///     The reader's structured document when <paramref name="Status" /> is
///     <see cref="DocumentExtractionStatus.Extracted" />; <see langword="null" /> for every other status.
/// </param>
/// <param name="Error">
///     A sanitized failure reason (never file content or the file name) when <paramref name="Status" /> is
///     <see cref="DocumentExtractionStatus.Failed" />; otherwise <see langword="null" />.
/// </param>
public sealed record DocumentStructuredExtractionResult(
    DocumentExtractionStatus Status,
    IngestionDocument? Document,
    string? Error);
