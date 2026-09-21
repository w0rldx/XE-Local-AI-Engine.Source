namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Represents node chat origin values.
/// </summary>
public static class NodeChatOriginValues
{
    public const string Local = "Local";
    public const string Remote = "Remote";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Local,
        Remote
    };
}

/// <summary>
///     Represents node chat message status values.
/// </summary>
public static class NodeChatMessageStatusValues
{
    public const string Pending = "pending";
    public const string Queued = "queued";
    public const string Streaming = "streaming";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
    public const string Interrupted = "interrupted";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Pending,
        Queued,
        Streaming,
        Completed,
        Cancelled,
        Failed,
        Interrupted
    };

    /// <summary>
    ///     The non-terminal statuses a message may still be cancelled from, so a late cancel on a finished row is
    ///     rejected without a rewrite. A conversation delete cancels its active messages on the same filter.
    /// </summary>
    public static readonly IReadOnlySet<string> Cancellable = new HashSet<string>(StringComparer.Ordinal)
    {
        Pending,
        Queued,
        Streaming
    };
}

public sealed record NodeChatMessageCorrelation
{
    public required Guid ConversationId { get; init; }

    public required Guid MessageId { get; init; }

    public required Guid RequestId { get; init; }
}

/// <summary>
///     The kind of an ordered assistant message part, from which the interleaved render region is reconstructed on
///     reload so the live and reloaded views match; <c>text</c> covers the rarer mid-turn narration case.
/// </summary>
public static class NodeChatMessagePartKinds
{
    public const string Reasoning = "reasoning";
    public const string Tool = "tool";
    public const string Text = "text";

    /// <summary>
    ///     A non-fatal turn notice (model substitution, tool disabled, history truncated). Reuses the generic
    ///     <see cref="NodeChatMessagePart.Text" /> for the sanitized message and <see cref="NodeChatMessagePart.Name" />
    ///     for the <c>TurnNoticeKind</c> enum name, rather than adding dedicated fields.
    /// </summary>
    public const string Notice = "notice";
}

/// <summary>
///     Represents the lifecycle state of a tool part. Mirrors the client tool-call state union; persisted tool parts
///     carry the terminal state (<see cref="Received" /> or <see cref="Failed" />) once the tool has completed.
/// </summary>
public static class NodeChatToolPartStates
{
    public const string Requesting = "requesting";
    public const string Waiting = "waiting";
    public const string Received = "received";
    public const string Failed = "failed";
}

/// <summary>
///     Represents node chat feedback rating values.
/// </summary>
public static class NodeChatFeedbackRatingValues
{
    public const string Up = "up";
    public const string Down = "down";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Up,
        Down
    };
}

/// <summary>
///     Thrown when a message-correlated write names no persisted message: the pair does not exist, or the request id
///     does not match the row it addresses.
/// </summary>
/// <remarks>
///     Endpoints that answer "not found" for a bad correlation catch THIS rather than
///     <see cref="InvalidOperationException" />, so an unrelated fault under the same call cannot present as a 404.
///     It still derives from that type, which the pump and the other correlated writers already read as "this write
///     cannot proceed". It is about the correlation, not about a message a person named, which
///     <c>NodeChatMessageNotFoundException</c> covers.
/// </remarks>
public sealed class NodeChatMessageCorrelationNotFoundException : InvalidOperationException
{
    public NodeChatMessageCorrelationNotFoundException(string message) : base(message)
    {
    }
}

/// <summary>
///     Thrown when a branch request's selected-revision entry fails integrity validation: its message is not in the
///     conversation, or it is keyed under a group it does not belong to.
/// </summary>
/// <remarks>
///     The branch endpoint maps it to HTTP 400. It fails closed, rejecting the branch rather than silently falling
///     back to a default revision.
/// </remarks>
public sealed class NodeChatInvalidBranchSelectionException : InvalidOperationException
{
    public const string Code = "invalid-branch-selection";

    public NodeChatInvalidBranchSelectionException(Guid conversationId, Guid variantGroupId, Guid messageId) : base($"Branch selection for conversation {conversationId} referenced message {messageId} which is not a valid member of variant group {variantGroupId}.")
    {
        ConversationId = conversationId;
        VariantGroupId = variantGroupId;
        MessageId = messageId;
    }

    public Guid ConversationId { get; }

    public Guid VariantGroupId { get; }

    public Guid MessageId { get; }
}
