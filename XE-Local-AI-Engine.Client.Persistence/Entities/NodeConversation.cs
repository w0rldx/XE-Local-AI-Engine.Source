namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class NodeConversation
{
    public Guid ConversationId { get; set; }

    public string? Title { get; set; }

    public string? UserId { get; set; }

    public long CreatedAtUtc { get; set; }

    public long LastSeenUtc { get; set; }

    public bool Purged { get; set; }

    public List<NodeMessage> Messages { get; } = [];

    public List<NodeToolEvent> ToolEvents { get; } = [];
}
