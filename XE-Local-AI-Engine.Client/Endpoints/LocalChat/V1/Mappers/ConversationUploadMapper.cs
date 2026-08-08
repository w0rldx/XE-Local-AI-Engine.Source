namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.DocumentIngestion;

internal static class ConversationUploadMapper
{
    public static ConversationUploadedFileResponse ToResponse(this ConversationUploadedFileInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        return new ConversationUploadedFileResponse
        {
            FileId = info.FileId,
            ConversationId = info.ConversationId,
            OriginalFileName = info.OriginalFileName,
            MimeType = info.MimeType,
            Extension = info.Extension,
            SizeBytes = info.SizeBytes,
            ExtractionStatus = info.ExtractionStatus.ToString(),
            ExtractedChars = info.ExtractedChars,
            CreatedAtUtc = info.CreatedAtUtc
        };
    }
}
