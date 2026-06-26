namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using Microsoft.AspNetCore.Http;

/// <summary>
///     Route binding for the multipart upload endpoint. The conversation id travels in the route; the file rides the
///     multipart form. The typed <see cref="File"/> property exists so FastEndpoints documents a
///     <c>multipart/form-data</c> request body in OpenAPI (the generated hey-api client then serializes the upload as
///     form-data rather than JSON). The handler still reads the form file collection directly, so the binding is
///     tolerant of the multipart field name the client chooses.
/// </summary>
public sealed class UploadConversationFileRequest
{
    public Guid ConversationId { get; init; }

    public IFormFile? File { get; init; }
}

public sealed class ListConversationUploadsRequest
{
    public Guid ConversationId { get; init; }
}

public sealed class DeleteConversationUploadRequest
{
    public Guid ConversationId { get; init; }

    public Guid FileId { get; init; }
}

/// <summary>
///     Metadata for one uploaded attachment. Never carries the raw bytes or extracted text — those are read on demand
///     by the agent (via its file tools) or inlined for plain chat.
/// </summary>
public sealed class ConversationUploadedFileResponse
{
    public required Guid FileId { get; init; }

    public required Guid ConversationId { get; init; }

    public required string OriginalFileName { get; init; }

    public required string MimeType { get; init; }

    public required string Extension { get; init; }

    public required long SizeBytes { get; init; }

    /// <summary>Extraction outcome name: <c>Pending</c> | <c>Extracted</c> | <c>Unsupported</c> | <c>Failed</c>.</summary>
    public required string ExtractionStatus { get; init; }

    public int? ExtractedChars { get; init; }

    public required long CreatedAtUtc { get; init; }
}

public sealed class ListConversationUploadsResponse
{
    public required IReadOnlyList<ConversationUploadedFileResponse> Items { get; init; }
}
