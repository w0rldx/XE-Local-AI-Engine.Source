namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Persistence boundary for evaluation runs and the comparison reports built from them. Same conventions as
///     <see cref="ITrainingRunStore" /> — hand-bumped <c>Version</c> tokens, explicit SQLite transactions, explicit
///     ordered deletes and the shared <see cref="TrainingStoreException" /> hierarchy. Evaluations ride the SAME durable
///     queue as training runs (<c>training_work_items</c> with <see cref="TrainingWorkKind.EvaluationRun" />), so the
///     claim, terminalize and recovery halves live in <see cref="ITrainingRunStore" /> and are not duplicated here.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Why the store owns the results merge.</strong> The per-sample verdicts are appended by a long loop
///         that can be interrupted at any point, and the three aggregate columns must never disagree with the blob they
///         summarize. Folding the by-sample-id merge and the aggregate recompute into one method makes that impossible
///         to get wrong per caller, and makes "recompute the accuracy from the persisted results" — the reproducibility
///         the comparison report rests on — a property of the data rather than of the writer.
///     </para>
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
    ///     A re-append of an already-scored sample is a silent no-op, which is what makes the resume path safe to
    ///     re-enter after an interruption. Does not bump <c>Version</c>: it fires once per sample from the single
    ///     executor that owns the evaluation, and bumping would invalidate that executor's expected version mid-loop.
    /// </summary>
    Task<TrainingEvaluationRecord> AppendResultsAsync(Guid evaluationId,
        IReadOnlyList<TrainingEvaluationResultEntry> entries,
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
    ///     Re-queues a terminated evaluation without discarding what it already scored. The frozen queue semantics pin
    ///     attempt to 1 and never retry a work item in place, so resume REPLACES the terminal work item with a fresh
    ///     queued one; the executor then continues from the next unscored sample. Refused while the evaluation is still
    ///     in flight, and refused once it has finished scoring its whole membership.
    /// </summary>
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
///     Everything an evaluation freezes at creation. <paramref name="TotalCount" /> is carried rather than derived from
///     the membership blob so "how far along is it" stays a plaintext query.
/// </summary>
public sealed record TrainingEvaluationEnqueueCommand(
    Guid? TrainingRunId,
    string ModelName,
    string? ModelContentFingerprint,
    Guid DatasetId,
    string DatasetContentFingerprint,
    ReadOnlyMemory<byte> MembershipJson,
    int TotalCount);

/// <summary>
///     An evaluation as the application layer sees it. The membership and the verdicts are carried as
///     <see cref="ReadOnlyMemory{T}" /> so the record cannot hand a caller a mutable reference to the decrypted column.
/// </summary>
public sealed record TrainingEvaluationRecord(
    Guid Id,
    Guid? TrainingRunId,
    Guid? ComparisonId,
    string ModelName,
    string? ModelContentFingerprint,
    Guid DatasetId,
    string DatasetContentFingerprint,
    ReadOnlyMemory<byte> MembershipJson,
    TrainingEvaluationStatus Status,
    ReadOnlyMemory<byte>? ResultsJson,
    int TotalCount,
    int ScoredCount,
    int PassedCount,
    string? PerKindJson,
    string? ErrorMessage,
    long Version,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    TrainingWorkStatus? WorkStatus);

/// <summary>
///     One sample's verdict. <paramref name="ScoredBy" /> carries provenance in the <c>DefaultPlaybookEvalJudge</c>
///     style — v1 only ever writes <c>deterministic</c>; <c>judge</c> is reserved for a follow-up LLM scorer.
/// </summary>
public sealed record TrainingEvaluationResultEntry(Guid SampleId, string Kind, bool Passed, string ScoredBy, string? Reason = null);

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

public sealed record TrainingComparisonInput(
    string Name,
    Guid BaseEvaluationRunId,
    Guid TunedEvaluationRunId,
    ReadOnlyMemory<byte> DeltasJson,
    Guid? BaseBenchmarkRunId = null,
    Guid? TunedBenchmarkRunId = null,
    Guid? TrainingRunId = null);

public sealed record TrainingComparisonRecord(
    Guid Id,
    string Name,
    Guid BaseEvaluationRunId,
    Guid TunedEvaluationRunId,
    Guid? BaseBenchmarkRunId,
    Guid? TunedBenchmarkRunId,
    Guid? TrainingRunId,
    ReadOnlyMemory<byte> DeltasJson,
    long Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);
