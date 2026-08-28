namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevWorkflowRunEvent
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid? NodeRunId { get; set; }
    public long Sequence { get; set; }
    public string EventType { get; set; } = string.Empty;

    /// <summary>Small structured payloads only — a gate reason, a failure summary, a materialization delta. Never a transcript.</summary>
    public byte[]? DetailJson { get; set; }

    public Guid? OperationId { get; set; }
    public string? Outcome { get; set; }
    public long OccurredAtUtc { get; set; }
}
