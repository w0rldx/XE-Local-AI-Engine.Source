namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class NodeConversation
{
    public Guid ConversationId { get; set; }

    public string? Title { get; set; }

    public string? UserId { get; set; }

    public long CreatedAtUtc { get; set; }

    public long LastSeenUtc { get; set; }

    public bool Purged { get; set; }

    public bool IsPinned { get; set; }

    public bool Archived { get; set; }

    public string Origin { get; set; } = NodeChatOrigin.Local;

    /// <summary>
    /// When this conversation was created by branching another, the source conversation id. Null for
    /// conversations that were not branched. Provenance only — branched rows are independent
    /// (Origin=Local) and never sync back.
    /// </summary>
    public Guid? BranchOfConversationId { get; set; }

    public List<NodeMessage> Messages { get; } = [];

    public List<NodeToolEvent> ToolEvents { get; } = [];
}
