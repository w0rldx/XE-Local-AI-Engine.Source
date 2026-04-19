namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class NodeToolEvent
{
    public Guid ToolCallId { get; set; }

    public Guid ConversationId { get; set; }

    public string ToolName { get; set; } = string.Empty;

    public byte[]? PlaintextArgs { get; set; }

    public byte[]? PlaintextResult { get; set; }

    public string Status { get; set; } = string.Empty;

    public long CreatedAtUtc { get; set; }

    public NodeConversation? Conversation { get; set; }
}
