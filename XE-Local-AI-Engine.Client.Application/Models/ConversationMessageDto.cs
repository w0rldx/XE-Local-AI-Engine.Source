namespace XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Transport DTO for conversation message data.
/// </summary>
public sealed record ConversationMessageDto
{
    public required Guid Id { get; init; }

    public required MessageRole Role { get; init; }

    public required string Content { get; init; }

    public string? Thinking { get; init; }

    public string? ModelUsed { get; init; }

    public required int SortOrder { get; init; }

    /// <summary>
    ///     TRANSIENT image parts for a vision (multimodal) turn — never persisted and never part of the encrypted history
    ///     hash (that hashes stored entries, not this turn DTO). Null on every text-only turn. Attached only when the
    ///     effective model is vision-capable; a non-vision model never receives image parts.
    /// </summary>
    public IReadOnlyList<ConversationImagePart>? Images { get; init; }

    /// <summary>
    ///     TRANSIENT tool call/result pairs replayed from an assistant turn's persisted parts, so a continued run reads
    ///     the actions it already performed rather than only the prose describing them. Follows the <see cref="Images" />
    ///     precedent exactly: attached by the turn assembler, never persisted, never part of the encrypted history hash
    ///     (whose input is the encrypted entry, which has no field for it), and always null on the inbound API-side
    ///     package path. Null on every turn that does not opt in — only the integration coordinator does, and only for a
    ///     caller-managed session.
    /// </summary>
    public IReadOnlyList<ConversationToolExchange>? ToolExchanges { get; init; }
}

/// <summary>
///     One completed tool call and its result, replayed into the model context as a
///     <c>FunctionCallContent</c> / <c>FunctionResultContent</c> pair.
/// </summary>
/// <param name="CallId">The provider's tool-call id, which correlates the call with its result.</param>
/// <param name="Name">The tool name.</param>
/// <param name="ArgumentsJson">
///     The raw argument JSON the call carried, or null when the provider gave none or it was never recorded.
/// </param>
/// <param name="Result">
///     The result text as the tool returned it, already excerpted to the historical tool-result budget.
/// </param>
/// <param name="IsError">
///     Whether the tool failed. The text is replayed either way, because the model acted on it either way.
/// </param>
public sealed record ConversationToolExchange(string CallId, string Name, string? ArgumentsJson, string? Result, bool IsError);

/// <summary>
///     One image attached to a turn: its IANA media type (e.g. <c>image/png</c>) and raw decoded bytes. Bytes ride as
///     <see cref="ReadOnlyMemory{T}" /> (the Application-layer convention for binary payloads, e.g.
///     <c>EncryptedConversationMessageDto</c>, and the exact shape Microsoft.Extensions.AI <c>DataContent</c> consumes);
///     a <c>byte[]</c> converts implicitly, so callers passing an array are unaffected.
/// </summary>
public sealed record ConversationImagePart(string MediaType, ReadOnlyMemory<byte> Data);
