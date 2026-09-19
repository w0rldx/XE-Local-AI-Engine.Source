namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     Decrypted metadata for one persisted uploaded file. Carries the display name and extraction summary the chat,
///     endpoint, and staging surfaces need — never the raw bytes or extracted text (those are read on demand).
/// </summary>
public sealed class ConversationUploadedFileInfo
{
    public required Guid FileId { get; init; }

    public required Guid ConversationId { get; init; }

    public required string OriginalFileName { get; init; }

    public required string MimeType { get; init; }

    public required string Extension { get; init; }

    public required long SizeBytes { get; init; }

    public required DocumentExtractionStatus ExtractionStatus { get; init; }

    public required int? ExtractedChars { get; init; }

    public required long CreatedAtUtc { get; init; }
}
