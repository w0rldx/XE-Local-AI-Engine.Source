namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

public sealed class ExportNodeChatConversationsRequest
{
    /// <summary>One chat to export; omitted exports every chat, archived ones included.</summary>
    public Guid? ConversationId { get; init; }
}

/// <summary>The chat export file: one or every chat, text only. An import posts the same document back.</summary>
/// <remarks>
///     Each chat is flattened to its selected branch path. Attachments and tool calls are recorded only as metadata
///     (file names, a count), never their bytes, arguments or results, and an import does not restore them.
/// </remarks>
public sealed class NodeChatExportDocument
{
    /// <summary>Must be <c>xe-chat-export</c>.</summary>
    public required string Format { get; init; }

    /// <summary>Must be 1, the only version this node writes.</summary>
    public required int FormatVersion { get; init; }

    public long ExportedAtUtc { get; init; }

    public required IReadOnlyList<NodeChatExportConversation> Conversations { get; init; }
}

public sealed class NodeChatExportConversation
{
    public string? Title { get; init; }

    public required long CreatedAtUtc { get; init; }

    public bool Archived { get; init; }

    /// <summary>Metadata of the files attached to the chat; their content is not exported.</summary>
    public IReadOnlyList<NodeChatExportAttachment> Attachments { get; init; } = [];

    /// <summary>The selected path in order.</summary>
    public required IReadOnlyList<NodeChatExportMessage> Messages { get; init; }
}

public sealed class NodeChatExportMessage
{
    /// <summary><c>user</c> or <c>assistant</c>.</summary>
    public required string Role { get; init; }

    public required string Content { get; init; }

    public required long CreatedAtUtc { get; init; }

    public string? Model { get; init; }

    /// <summary>How many tool calls the turn made; their arguments and results are not exported.</summary>
    public int ToolCallCount { get; init; }
}

public sealed class NodeChatExportAttachment
{
    public required string FileName { get; init; }

    public string? MimeType { get; init; }

    public long SizeBytes { get; init; }
}

public sealed class NodeChatImportResponse
{
    public required IReadOnlyList<NodeChatImportedConversationResponse> Conversations { get; init; }
}

public sealed class NodeChatImportedConversationResponse
{
    /// <summary>The NEW id; an import never reuses an id from the file.</summary>
    public required Guid ConversationId { get; init; }

    public required string? Title { get; init; }

    public required int MessageCount { get; init; }
}
