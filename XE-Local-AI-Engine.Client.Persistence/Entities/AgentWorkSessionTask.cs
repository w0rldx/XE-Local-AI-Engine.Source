namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class AgentWorkSessionTask
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid? ParentTaskId { get; set; }
    public long Sequence { get; set; }
    public byte[] Title { get; set; } = [];
    public byte[]? Detail { get; set; }
    public AgentWorkSessionTaskStatus Status { get; set; }
    public byte[]? BlockedReason { get; set; }
    public AgentWorkSessionTaskOrigin Origin { get; set; }
    public int CreatedStep { get; set; }
    public int UpdatedStep { get; set; }
}
