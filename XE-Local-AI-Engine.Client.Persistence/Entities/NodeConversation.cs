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
    ///     When this conversation was created by branching another, the source conversation id. Null for
    ///     conversations that were not branched. Provenance only — branched rows are independent
    ///     (Origin=Local) and never sync back.
    /// </summary>
    public Guid? BranchOfConversationId { get; set; }

    /// <summary>
    ///     JSON metadata map of variantGroupId-&gt;selectedMessageId capturing which sibling variant is selected on each
    ///     branched turn. Topology lives on the messages (parent/variant-group); this column is selection metadata only,
    ///     so it is additive, nullable, and E2E-safe (never required to reconstruct the conversation tree).
    /// </summary>
    public string? SelectedPathJson { get; set; }

    /// <summary>
    ///     The node-local agent definition this conversation is bound to, or null for the implicit default persona.
    ///     A loose nullable Guid with no enforced FK (mirrors <see cref="BranchOfConversationId" />): a binding that
    ///     points at a deleted definition is treated as null by the resolver rather than failing the read.
    /// </summary>
    public Guid? AgentDefinitionId { get; set; }

    public List<NodeMessage> Messages { get; } = [];

    public List<NodeToolEvent> ToolEvents { get; } = [];
}
