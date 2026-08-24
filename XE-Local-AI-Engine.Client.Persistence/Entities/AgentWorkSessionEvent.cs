namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class AgentWorkSessionEvent
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public long Sequence { get; set; }
    public int Step { get; set; }
    public string EventType { get; set; } = string.Empty;
    public byte[]? DetailJson { get; set; }
    public Guid? OperationId { get; set; }
    public string? Outcome { get; set; }
    public long OccurredAtUtc { get; set; }
}
