namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Base versus tuned, bound to the two evaluations it was computed from. The deltas are stored rather than derived
///     on read so a report keeps meaning after its inputs are re-run — but they stay REPRODUCIBLE from the bound
///     evaluations' persisted results, which is what the reproducibility test pins.
/// </summary>
internal sealed record class TrainingComparisonReport
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The evaluation of the untuned model. Real FK to <c>training_evaluation_runs.id</c>, restricted delete.</summary>
    public Guid BaseEvaluationRunId { get; set; }

    public Guid TunedEvaluationRunId { get; set; }

    /// <summary>Optional throughput/quality pairing. No foreign key: a benchmark run is deletable on its own schedule.</summary>
    public Guid? BaseBenchmarkRunId { get; set; }

    public Guid? TunedBenchmarkRunId { get; set; }

    /// <summary>The run this comparison is about, when it came from one. Lineage only; indexed.</summary>
    public Guid? TrainingRunId { get; set; }

    /// <summary>
    ///     The computed deltas as UTF-8 JSON. Plaintext while tracked in memory; encrypted at rest under AAD column
    ///     name <c>training_comparison_deltas_json</c>. Required.
    /// </summary>
    public byte[] DeltasJson { get; set; } = [];

    public long Version { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}
