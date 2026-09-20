namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     Everything the file store needs to durably persist one uploaded file: the raw bytes plus the already-computed
///     extraction outcome.
/// </summary>
/// <remarks>
///     Extraction runs at the endpoint, before this is constructed; the store only persists and encrypts.
///     <see cref="ExtractedMarkdown" /> is non-null only when <see cref="ExtractionStatus" /> is
///     <see cref="DocumentExtractionStatus.Extracted" />.
/// </remarks>
public sealed record ConversationUploadedFileInput
{
    public required Guid ConversationId { get; init; }

    public required Guid FileId { get; init; }

    public required string OriginalFileName { get; init; }

    public required string MimeType { get; init; }

    public required string Extension { get; init; }

    public required long SizeBytes { get; init; }

    public required ReadOnlyMemory<byte> Content { get; init; }

    public required DocumentExtractionStatus ExtractionStatus { get; init; }

    public required string? ExtractedMarkdown { get; init; }

    public required int? ExtractedChars { get; init; }
}
