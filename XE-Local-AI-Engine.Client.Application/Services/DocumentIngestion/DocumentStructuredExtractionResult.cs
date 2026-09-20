namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

using Microsoft.Extensions.DataIngestion;

/// <summary>
///     Outcome of extracting the structured <see cref="IngestionDocument" /> — sections, headers, paragraphs — from an
///     uploaded document, before it is flattened to Markdown.
/// </summary>
/// <remarks>
///     Mirrors <see cref="DocumentExtractionResult" /> but carries the reader's structured document instead of a Markdown
///     string, so the chunking lane can walk the heading structure.
/// </remarks>
public sealed class DocumentStructuredExtractionResult
{
    /// <summary>How the extraction resolved.</summary>
    public required DocumentExtractionStatus Status { get; init; }

    /// <summary>
    ///     The reader's structured document when <see cref="Status" /> is
    ///     <see cref="DocumentExtractionStatus.Extracted" />; <see langword="null" /> for every other status.
    /// </summary>
    public required IngestionDocument? Document { get; init; }

    /// <summary>
    ///     A sanitized failure reason (never file content or the file name) when <see cref="Status" /> is
    ///     <see cref="DocumentExtractionStatus.Failed" />; otherwise <see langword="null" />.
    /// </summary>
    public required string? Error { get; init; }
}
