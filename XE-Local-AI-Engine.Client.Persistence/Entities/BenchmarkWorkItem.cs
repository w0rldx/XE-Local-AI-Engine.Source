namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class BenchmarkWorkItem
{
    public long QueueSequence { get; set; }
    public Guid RunId { get; set; }
    public BenchmarkWorkKind Kind { get; set; }

    /// <summary>
    ///     The judge attempt this item judges. Set exactly when <see cref="Kind" /> is
    ///     <see cref="BenchmarkWorkKind.Judge" />, and the uniqueness key for judge work — a run has many attempts.
    /// </summary>
    public Guid? JudgeAttemptId { get; set; }
    public BenchmarkWorkStatus Status { get; set; }
    public int Attempt { get; set; }
    public long Version { get; set; }
    public long EnqueuedAtUtc { get; set; }
    public long? StartedAtUtc { get; set; }
    public long? FinishedAtUtc { get; set; }
    public string? ErrorMessage { get; set; }
}
