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

    public Guid? RequestId { get; set; }

    public string? Error { get; set; }

    public NodeConversation? Conversation { get; set; }
}
