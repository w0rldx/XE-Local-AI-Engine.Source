namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Persistence boundary for evaluation runs and the comparison reports built from them.
/// </summary>
/// <remarks>
///     Same conventions as <see cref="ITrainingRunStore" /> — hand-bumped <c>Version</c> tokens, explicit SQLite
///     transactions, explicit ordered deletes, the shared <see cref="TrainingStoreException" /> hierarchy — and the
///     SAME durable queue (<c>training_work_items</c> with <see cref="TrainingWorkKind.EvaluationRun" />), so the
///     claim, terminalize and recovery halves live in <see cref="ITrainingRunStore" />. The store owns the results
///     merge so the aggregates can never disagree with the blob they summarize: see <see cref="AppendResultsAsync" />.
/// </remarks>
public interface ITrainingEvaluationStore
{
    /// <summary>
    ///     Creates the evaluation and its single queued work item in one transaction. Refuses a membership that is
    ///     empty or a total count that does not describe one.
    /// </summary>
    Task<TrainingEvaluationRecord> CreateAndEnqueueAsync(TrainingEvaluationEnqueueCommand command, CancellationToken cancellationToken = default);

    Task<TrainingEvaluationRecord?> GetAsync(Guid evaluationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrainingEvaluationRecord>> ListAsync(Guid? trainingRunId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Merges verdicts into the results blob keyed by sample id and recomputes every aggregate from the merged set.
    /// </summary>
    /// <remarks>
    ///     The verdicts are appended by a long loop that can be interrupted at any point, so folding the by-sample-id
    ///     merge and the aggregate recompute into one method keeps "recompute the accuracy from the persisted
    ///     results" — the reproducibility the comparison report rests on — a property of the data, not of the writer.
    ///     A re-append of an already-scored sample is a silent no-op, which makes the resume path safe to re-enter.
    ///     Does not bump <c>Version</c>: it fires once per sample from the evaluation's single executor.
    /// </remarks>
    Task<TrainingEvaluationRecord> AppendResultsAsync(Guid evaluationId,
        IReadOnlyList<TrainingEvaluationResultEntry> entries,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Binds the runtime identity before the first verdict is appended. A partial evaluation may resume only when
    ///     the new attempt presents byte-identical provenance; otherwise verdicts from two runtimes would be mixed.
    /// </summary>
    Task<TrainingEvaluationRecord> BindExecutionProvenanceAsync(Guid evaluationId,
        ReadOnlyMemory<byte> provenanceJson,
        CancellationToken cancellationToken = default);

    /// <summary>Moves a queued evaluation to <see cref="TrainingEvaluationStatus.Running" />. Terminal is rejected here.</summary>
    Task<TrainingEvaluationRecord> TransitionAsync(Guid evaluationId,
        long expectedVersion,
        TrainingEvaluationStatus status,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Terminalizes the evaluation's work item and the evaluation itself in one transaction. Idempotent: a second
    ///     call on an already-terminal work item is a silent no-op.
    /// </summary>
    Task<TrainingEvaluationRecord> CompleteAsync(Guid evaluationId,
        TrainingWorkStatus status,
        string? errorMessage,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Re-queues a terminated evaluation without discarding what it already scored. Refused while it is still in
    ///     flight, and refused once it has finished scoring its whole membership.
    /// </summary>
    /// <remarks>
    ///     The frozen queue semantics pin attempt to 1 and never retry a work item in place, so resume REPLACES the
    ///     terminal work item with a fresh queued one. Before continuing from the next unscored sample the executor
    ///     must bind byte-identical execution provenance to the partial attempt; a different runtime identity is
    ///     refused rather than mixing verdicts.
    /// </remarks>
    Task<TrainingEvaluationRecord> ResumeAsync(Guid evaluationId, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes the evaluation and its work item, in that order. Refused while the work item is non-terminal, and
    ///     refused while a comparison report is bound to it — a report whose input vanished carries deltas nothing can
    ///     reproduce.
    /// </summary>
    Task DeleteAsync(Guid evaluationId, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Creates the report and binds its two evaluations by stamping their <c>comparison_id</c>, in one transaction.
    ///     Refuses an evaluation that is already bound to another report, and refuses the same evaluation on both sides.
    /// </summary>
    Task<TrainingComparisonRecord> CreateComparisonAsync(TrainingComparisonInput input, CancellationToken cancellationToken = default);

    Task<TrainingComparisonRecord?> GetComparisonAsync(Guid comparisonId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrainingComparisonRecord>> ListComparisonsAsync(CancellationToken cancellationToken = default);

    /// <summary>Unbinds the two evaluations, then deletes the report — the ordered-delete shape the whole module uses.</summary>
    Task DeleteComparisonAsync(Guid comparisonId, long expectedVersion, CancellationToken cancellationToken = default);
}

/// <summary>
///     Everything an evaluation freezes at creation. <see cref="TotalCount" /> is carried rather than derived from
///     the membership blob so "how far along is it" stays a plaintext query.
/// </summary>
public sealed record TrainingEvaluationEnqueueCommand
{
    public required Guid? TrainingRunId { get; init; }

    public required string ModelName { get; init; }

    public required string? ModelContentFingerprint { get; init; }

    public required Guid DatasetId { get; init; }

    public required string DatasetContentFingerprint { get; init; }

    public required ReadOnlyMemory<byte> MembershipJson { get; init; }

    public required int TotalCount { get; init; }

    public EvaluationModelTargetKind TargetKind { get; init; }

    public Guid? SourceArtifactId { get; init; }
}

/// <summary>
///     An evaluation as the application layer sees it. The membership and the verdicts are carried as
///     <see cref="ReadOnlyMemory{T}" /> so the record cannot hand a caller a mutable reference to the decrypted column.
/// </summary>
public sealed record TrainingEvaluationRecord
{
    public required Guid Id { get; init; }

    public required Guid? TrainingRunId { get; init; }

    public required Guid? ComparisonId { get; init; }

    public required string ModelName { get; init; }

    public required string? ModelContentFingerprint { get; init; }

    public required Guid DatasetId { get; init; }

    public required string DatasetContentFingerprint { get; init; }

    public required ReadOnlyMemory<byte> MembershipJson { get; init; }

    public required TrainingEvaluationStatus Status { get; init; }

    public required ReadOnlyMemory<byte>? ResultsJson { get; init; }

    public required int TotalCount { get; init; }

    public required int ScoredCount { get; init; }

    public required int PassedCount { get; init; }

    public required string? PerKindJson { get; init; }

    public required string? ErrorMessage { get; init; }

    public required long Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required TrainingWorkStatus? WorkStatus { get; init; }

    public EvaluationModelTargetKind TargetKind { get; init; }

    public Guid? SourceArtifactId { get; init; }

    public ReadOnlyMemory<byte>? ExecutionProvenanceJson { get; init; }
}

/// <summary>
///     One sample's verdict. <see cref="ScoredBy" /> carries provenance in the <c>DefaultPlaybookEvalJudge</c>
///     style. Version 1 writes <c>deterministic</c>; no current scorer writes the reserved <c>judge</c> value.
/// </summary>
public sealed class TrainingEvaluationResultEntry
{
    public required Guid SampleId { get; init; }

    public required string Kind { get; init; }

    public required bool Passed { get; init; }

    public required string ScoredBy { get; init; }

    public string? Reason { get; init; }
}

/// <summary>The persisted results document. One flat list — the merge is by <c>SampleId</c>, so no nesting buys anything.</summary>
public sealed record TrainingEvaluationResultsV1
{
    public int SchemaVersion { get; init; } = 1;

    public IReadOnlyList<TrainingEvaluationResultEntry> Entries { get; init; } = [];
}

/// <summary>One kind's tally, as the plaintext <c>per_kind_json</c> column carries it.</summary>
public sealed record TrainingEvaluationKindTally(int Total, int Passed);

/// <summary>
///     Reading and writing the two derived documents — the results blob and the per-kind tally. Shared by the store
///     (which writes them) and the comparison service (which recomputes deltas from them), so the two cannot drift.
/// </summary>
public static class TrainingEvaluationResults
{
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>Returns an empty list rather than throwing on a legacy or unreadable blob — a corrupt tail must not strand a resume.</summary>
    public static IReadOnlyList<TrainingEvaluationResultEntry> Read(ReadOnlyMemory<byte>? resultsJson)
    {
        if (resultsJson is not { } bytes || bytes.IsEmpty)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<TrainingEvaluationResultsV1>(bytes.Span, Options)?.Entries ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static byte[] Write(IReadOnlyList<TrainingEvaluationResultEntry> entries) =>
        JsonSerializer.SerializeToUtf8Bytes(new TrainingEvaluationResultsV1
        {
            Entries = entries
        }, Options);

    public static IReadOnlyDictionary<string, TrainingEvaluationKindTally> Tally(IReadOnlyList<TrainingEvaluationResultEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var tally = new Dictionary<string, TrainingEvaluationKindTally>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var kind = string.IsNullOrWhiteSpace(entry.Kind) ? "unknown" : entry.Kind;
            var current = tally.TryGetValue(kind, out var found) ? found : new TrainingEvaluationKindTally(Total: 0, Passed: 0);
            tally[kind] = new TrainingEvaluationKindTally(current.Total + 1, current.Passed + (entry.Passed ? 1 : 0));
        }

        return tally;
    }

    public static string WriteTally(IReadOnlyDictionary<string, TrainingEvaluationKindTally> tally) =>
        JsonSerializer.Serialize(tally, Options);

    /// <summary>Returns an empty tally rather than throwing — the column is advisory, and the entries are authoritative.</summary>
    public static IReadOnlyDictionary<string, TrainingEvaluationKindTally> ReadTally(string? perKindJson)
    {
        if (string.IsNullOrWhiteSpace(perKindJson))
        {
            return new Dictionary<string, TrainingEvaluationKindTally>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, TrainingEvaluationKindTally>>(perKindJson, Options)
                   ?? new Dictionary<string, TrainingEvaluationKindTally>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, TrainingEvaluationKindTally>(StringComparer.Ordinal);
        }
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed record TrainingComparisonInput
{
    public required string Name { get; init; }

    public required Guid BaseEvaluationRunId { get; init; }

    public required Guid TunedEvaluationRunId { get; init; }

    public required ReadOnlyMemory<byte> DeltasJson { get; init; }

    public Guid? BaseBenchmarkRunId { get; init; }

    public Guid? TunedBenchmarkRunId { get; init; }

    public Guid? TrainingRunId { get; init; }
}

public sealed record TrainingComparisonRecord
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required Guid BaseEvaluationRunId { get; init; }

    public required Guid TunedEvaluationRunId { get; init; }

    public required Guid? BaseBenchmarkRunId { get; init; }

    public required Guid? TunedBenchmarkRunId { get; init; }

    public required Guid? TrainingRunId { get; init; }

    public required ReadOnlyMemory<byte> DeltasJson { get; init; }

    public required long Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}
