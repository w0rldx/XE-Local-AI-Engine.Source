namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Durable FIFO row for the training queue — the <see cref="BenchmarkWorkItem" /> pattern duplicated once more.
///     Unlike the benchmark and dataset queues, <see cref="TargetId" /> is polymorphic over <see cref="Kind" />, so it
///     carries no foreign key: a run id and an evaluation id live in different tables.
/// </summary>
internal sealed record class TrainingWorkItem
{
    public long QueueSequence { get; set; }

    public TrainingWorkKind Kind { get; set; }

    /// <summary>The run id (<see cref="TrainingWorkKind.TrainingRun" />) or evaluation id this item executes.</summary>
    public Guid TargetId { get; set; }

    public TrainingWorkStatus Status { get; set; }

    /// <summary>Pinned to 1 by a check constraint: an interrupted run is terminalized as failed, never retried in place.</summary>
    public int Attempt { get; set; }

    /// <summary>Optimistic-concurrency token — the <c>Queued -&gt; Running</c> claim is an ordinary versioned UPDATE.</summary>
    public long Version { get; set; }

    public long EnqueuedAtUtc { get; set; }

    public long? StartedAtUtc { get; set; }

    public long? FinishedAtUtc { get; set; }

    public string? ErrorMessage { get; set; }
}
