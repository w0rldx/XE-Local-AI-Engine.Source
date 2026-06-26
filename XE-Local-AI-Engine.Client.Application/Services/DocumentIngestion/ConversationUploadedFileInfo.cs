namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     Decrypted metadata for one persisted uploaded file. Carries the display name and extraction summary the chat,
///     endpoint, and staging surfaces need — never the raw bytes or extracted text (those are read on demand).
/// </summary>
public sealed record ConversationUploadedFileInfo(
    Guid FileId,
    Guid ConversationId,
    string OriginalFileName,
    string MimeType,
    string Extension,
    long SizeBytes,
    DocumentExtractionStatus ExtractionStatus,
    int? ExtractedChars,
    long CreatedAtUtc);
