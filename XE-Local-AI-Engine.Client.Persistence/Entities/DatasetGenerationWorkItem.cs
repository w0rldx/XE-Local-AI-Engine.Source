namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Durable FIFO row for the dataset-generation queue — the <see cref="BenchmarkWorkItem" /> pattern duplicated
///     rather than generalized. Dataset generation has a single work kind, so the benchmark's <c>kind</c> column and its
///     <c>(run_id, kind)</c> uniqueness collapse into one work item per dataset.
/// </summary>
internal sealed record class DatasetGenerationWorkItem
{
    public long QueueSequence { get; set; }

    public Guid DatasetId { get; set; }

    public DatasetGenerationWorkStatus Status { get; set; }

    /// <summary>Pinned to 1 by a check constraint: an interrupted run is terminalized as failed, never retried in place.</summary>
    public int Attempt { get; set; }

    /// <summary>Optimistic-concurrency token — the <c>Queued -&gt; Running</c> claim is an ordinary versioned UPDATE.</summary>
    public long Version { get; set; }

    public long EnqueuedAtUtc { get; set; }

    public long? StartedAtUtc { get; set; }

    public long? FinishedAtUtc { get; set; }

    public string? ErrorMessage { get; set; }
}
