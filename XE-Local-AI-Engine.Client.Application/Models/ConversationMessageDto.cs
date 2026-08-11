namespace XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Transport DTO for conversation message data.
/// </summary>
public sealed record ConversationMessageDto
{
    public required Guid Id { get; init; }

    public required MessageRole Role { get; init; }

    public required string Content { get; init; }

    public string? ToolCalls { get; init; }

    public string? ToolResults { get; init; }

    public string? Thinking { get; init; }

    public string? ModelUsed { get; init; }

    public required int SortOrder { get; init; }

    /// <summary>
    ///     TRANSIENT image parts for a vision (multimodal) turn — never persisted and never part of the encrypted history
    ///     hash (that hashes stored entries, not this turn DTO). Null on every text-only turn. Attached only when the
    ///     effective model is vision-capable; a non-vision model never receives image parts.
    /// </summary>
    public IReadOnlyList<ConversationImagePart>? Images { get; init; }
}

/// <summary>
///     One image attached to a turn: its IANA media type (e.g. <c>image/png</c>) and raw decoded bytes. Bytes ride as
///     <see cref="ReadOnlyMemory{T}" /> (the Application-layer convention for binary payloads, e.g.
///     <c>EncryptedConversationMessageDto</c>, and the exact shape Microsoft.Extensions.AI <c>DataContent</c> consumes);
///     a <c>byte[]</c> converts implicitly, so callers passing an array are unaffected.
/// </summary>
public sealed record ConversationImagePart(string MediaType, ReadOnlyMemory<byte> Data);
