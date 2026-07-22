namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevelopmentEvent
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? TaskId { get; set; }
    public Guid? AttemptId { get; set; }
    public long Sequence { get; set; }
    public string EventType { get; set; } = string.Empty;
    public long OccurredAtUtc { get; set; }
    public byte[]? DetailJson { get; set; }
    public Guid? OperationId { get; set; }
    public string? OperationPhase { get; set; }
    public string? Outcome { get; set; }
    public byte[]? ResultMetadataJson { get; set; }
}

