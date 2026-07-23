namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevelopmentAttempt
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid? PredecessorAttemptId { get; set; }
    public DevelopmentAttemptRole Role { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public DevelopmentAttemptStatus Status { get; set; }
    public long? StartedAtUtc { get; set; }
    public long? EndedAtUtc { get; set; }
    public string? TerminalReason { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public Guid StartOperationId { get; set; }
    public long Version { get; set; }
}
