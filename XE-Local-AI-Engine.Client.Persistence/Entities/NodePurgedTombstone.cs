namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class NodePurgedTombstone
{
    public Guid ConversationId { get; set; }

    public long PurgedAtUtc { get; set; }

    public long? AckedAtUtc { get; set; }
}
