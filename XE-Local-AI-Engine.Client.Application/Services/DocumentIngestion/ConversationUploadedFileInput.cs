namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     Everything the file store needs to durably persist one uploaded file: the raw bytes plus the already-computed
///     extraction outcome. Extraction runs at the endpoint (before this is constructed); the store only persists +
///     encrypts. <paramref name="ExtractedMarkdown"/> is non-null only when <paramref name="ExtractionStatus"/> is
///     <see cref="DocumentExtractionStatus.Extracted"/>.
/// </summary>
public sealed record ConversationUploadedFileInput(
    Guid ConversationId,
    Guid FileId,
    string OriginalFileName,
    string MimeType,
    string Extension,
    long SizeBytes,
    ReadOnlyMemory<byte> Content,
    DocumentExtractionStatus ExtractionStatus,
    string? ExtractedMarkdown,
    int? ExtractedChars);
