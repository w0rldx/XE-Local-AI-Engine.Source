namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class NodeMessage
{
    public Guid MessageId { get; set; }

    public Guid ConversationId { get; set; }

    public int Sequence { get; set; }

    public string Role { get; set; } = string.Empty;

    public byte[] Content { get; set; } = [];

    public byte[]? MetadataJson { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }

    public string Status { get; set; } = NodeMessageStatus.Completed;

    public string Origin { get; set; } = NodeChatOrigin.Local;

    public Guid? RequestId { get; set; }

    public string? Error { get; set; }

    /// <summary>
    /// The message this one descends from (branch/revision provenance). Null for original turns.
    /// </summary>
    public Guid? ParentMessageId { get; set; }

    /// <summary>
    /// Groups sibling variants of one logical turn (regenerate produces siblings, never an in-place
    /// overwrite). All variants of a turn share this id; the active variant is selected client-side. Null
    /// for turns that have never been regenerated.
    /// </summary>
    public Guid? VariantGroupId { get; set; }

    public NodeConversation? Conversation { get; set; }
}
