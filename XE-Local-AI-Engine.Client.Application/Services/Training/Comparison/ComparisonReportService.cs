namespace XE_Local_AI_Engine.Client.Services.Training.Comparison;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;
using XE_Local_AI_Engine.Client.Services.Training.Export;

/// <summary>What the operator asked to compare. The benchmark ids are optional and validated to exist before binding.</summary>
public sealed record CreateComparisonCommand(
    string Name,
    Guid BaseEvaluationRunId,
    Guid TunedEvaluationRunId,
    Guid? BaseBenchmarkRunId = null,
    Guid? TunedBenchmarkRunId = null,
    Guid? TrainingRunId = null);

public interface IComparisonReportService
{
    Task<TrainingComparisonRecord> CreateAsync(CreateComparisonCommand command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrainingComparisonRecord>> ListAsync(CancellationToken cancellationToken = default);

    Task<TrainingComparisonRecord?> GetAsync(Guid comparisonId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid comparisonId, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     What a create dialog should pre-fill for one training run: the two model names its lineage implies, and the
    ///     evaluations that already exist for them.
    /// </summary>
    Task<ComparisonSuggestion> SuggestAsync(Guid trainingRunId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Builds and reads comparison reports. The deltas are computed once at creation and stored, but
///     <see cref="ComputeDeltas" /> stays a pure function of the two evaluation records so the same numbers can be
///     recomputed from storage and checked against what was stored.
/// </summary>
public sealed class ComparisonReportService(
    ITrainingEvaluationStore evaluations,
    ITrainingRunStore runs,
    IBenchmarkStore benchmarks) : IComparisonReportService
{
    private readonly IBenchmarkStore _benchmarks = benchmarks ?? throw new ArgumentNullException(nameof(benchmarks));
    private readonly ITrainingEvaluationStore _evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));
    private readonly ITrainingRunStore _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    public async Task<TrainingComparisonRecord> CreateAsync(CreateComparisonCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            throw new EvaluationRejectedException("A comparison report needs a name.");
        }

        var baseEvaluation = await _evaluations.GetAsync(command.BaseEvaluationRunId, cancellationToken).ConfigureAwait(false)
                             ?? throw new EvaluationRejectedException("The base evaluation run was not found.");
        var tunedEvaluation = await _evaluations.GetAsync(command.TunedEvaluationRunId, cancellationToken).ConfigureAwait(false)
                              ?? throw new EvaluationRejectedException("The tuned evaluation run was not found.");

        EnsureSuccessfullyComplete(baseEvaluation, "base");
        EnsureSuccessfullyComplete(tunedEvaluation, "tuned");
        var trainingRunId = EnsureComparable(baseEvaluation, tunedEvaluation);

        var baseBenchmark = await RequireBenchmarkAsync(command.BaseBenchmarkRunId, cancellationToken).ConfigureAwait(false);
        var tunedBenchmark = await RequireBenchmarkAsync(command.TunedBenchmarkRunId, cancellationToken).ConfigureAwait(false);

        var deltas = ComputeDeltas(baseEvaluation, tunedEvaluation, baseBenchmark, tunedBenchmark);
        return await _evaluations.CreateComparisonAsync(new TrainingComparisonInput(command.Name.Trim(),
                                         baseEvaluation.Id,
                                         tunedEvaluation.Id,
                                         JsonSerializer.SerializeToUtf8Bytes(deltas, TrainingJson.Options),
                                         command.BaseBenchmarkRunId,
                                         command.TunedBenchmarkRunId,
                                         trainingRunId),
                                     cancellationToken)
                                 .ConfigureAwait(false);
    }

    public Task<IReadOnlyList<TrainingComparisonRecord>> ListAsync(CancellationToken cancellationToken = default) =>
        _evaluations.ListComparisonsAsync(cancellationToken);

    public Task<TrainingComparisonRecord?> GetAsync(Guid comparisonId, CancellationToken cancellationToken = default) =>
        _evaluations.GetComparisonAsync(comparisonId, cancellationToken);

    public async Task DeleteAsync(Guid comparisonId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        var comparison = await _evaluations.GetComparisonAsync(comparisonId, cancellationToken).ConfigureAwait(false);
        if (comparison is not null)
        {
            var baseEvaluation = await _evaluations.GetAsync(comparison.BaseEvaluationRunId, cancellationToken).ConfigureAwait(false);
            var tunedEvaluation = await _evaluations.GetAsync(comparison.TunedEvaluationRunId, cancellationToken).ConfigureAwait(false);
            var possibleRunIds = new[] { comparison.TrainingRunId, baseEvaluation?.TrainingRunId, tunedEvaluation?.TrainingRunId }
                                 .OfType<Guid>()
                                 .Distinct();
            foreach (var runId in possibleRunIds)
            {
                var artifacts = await _runs.ListArtifactsAsync(runId, cancellationToken).ConfigureAwait(false);
                if (artifacts.Select(ArtifactQualityService.ReadDecision)
                             .Where(static decision => decision is not null)
                             .Any(decision => decision!.History.Any(item => item.ComparisonId == comparisonId)))
                {
                    throw new EvaluationRejectedException("A comparison retained in artifact quality audit history cannot be deleted.");
                }
            }
        }

        await _evaluations.DeleteComparisonAsync(comparisonId, expectedVersion, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ComparisonSuggestion> SuggestAsync(Guid trainingRunId, CancellationToken cancellationToken = default)
    {
        var run = await _runs.GetAsync(trainingRunId, cancellationToken).ConfigureAwait(false)
                  ?? throw new EvaluationRejectedException("The training run was not found.");

        var artifacts = await _runs.ListArtifactsAsync(trainingRunId, cancellationToken).ConfigureAwait(false);
        var tunedArtifact = artifacts.FirstOrDefault(artifact => artifact.Kind != TrainingArtifactKind.HfAdapterDir
                                                                  && artifact.DiscardedAtUtc is null
                                                                  && !string.IsNullOrWhiteSpace(artifact.Sha256));
        var tunedModelName = tunedArtifact is null ? null : Path.GetFileName(tunedArtifact.Path);
        var existing = await _evaluations.ListAsync(trainingRunId, cancellationToken).ConfigureAwait(false);

        // Matched by model name rather than by a stored "side" flag: the side an evaluation is on IS which model it
        // scored, and one stored flag that disagreed with the model name would be a second truth to keep in sync.
        var baseEvaluation = existing.FirstOrDefault(item => Matches(item.ModelName, run.LinkedInstalledModelName));
        var tunedEvaluation = existing.FirstOrDefault(item => item.TargetKind == EvaluationModelTargetKind.StagedTrainingArtifact
                                                               && item.SourceArtifactId == tunedArtifact?.Id);

        var reason = UnavailableReason(run.LinkedInstalledModelName, tunedModelName);
        return new ComparisonSuggestion(trainingRunId,
            run.LinkedInstalledModelName,
            tunedModelName,
            baseEvaluation?.Id,
            tunedEvaluation?.Id,
            reason);
    }

    /// <summary>
    ///     The precondition a delta rests on: both sides scored the SAME hold-out samples, of the same version of the
    ///     same dataset. Subtracting two accuracies measured over different sample sets produces a number that looks
    ///     exactly like a real improvement, so this is refused rather than reported.
    /// </summary>
    private static Guid EnsureComparable(TrainingEvaluationRecord baseEvaluation, TrainingEvaluationRecord tunedEvaluation)
    {
        var left = ReadMembership(baseEvaluation, "base");
        var right = ReadMembership(tunedEvaluation, "tuned");

        if (baseEvaluation.TrainingRunId is not { } trainingRunId
            || tunedEvaluation.TrainingRunId != trainingRunId
            || left.TrainingRunId != trainingRunId
            || right.TrainingRunId != trainingRunId)
        {
            throw new EvaluationRejectedException("Both evaluations must belong to the same training run and frozen run lineage.");
        }

        if (left.DatasetId != right.DatasetId)
        {
            throw new EvaluationRejectedException("The two evaluations scored different datasets, so their accuracies cannot be subtracted. Evaluate both models against one dataset.");
        }

        if (!string.Equals(left.DatasetContentFingerprint, right.DatasetContentFingerprint, StringComparison.Ordinal))
        {
            throw new EvaluationRejectedException(
                "The two evaluations scored different versions of the dataset — their content fingerprints differ, so the dataset changed between them. Re-evaluate both models against one freeze.");
        }

        // Order-insensitive: scoring walks the frozen order, but two freezes of one set are still the same hold-out.
        if (!left.HoldoutSampleIds.ToHashSet().SetEquals(right.HoldoutSampleIds))
        {
            throw new EvaluationRejectedException("The two evaluations scored different hold-out samples, so their accuracies are not comparable. Both sides must come from the same freeze.");
        }

        return trainingRunId;
    }

    private static void EnsureSuccessfullyComplete(TrainingEvaluationRecord evaluation, string side)
    {
        var membership = ReadMembership(evaluation, side);
        var entries = TrainingEvaluationResults.Read(evaluation.ResultsJson);
        var resultIds = entries.Select(entry => entry.SampleId).ToHashSet();
        var fullyScored = evaluation.Status == TrainingEvaluationStatus.Succeeded
                          && evaluation.WorkStatus == TrainingWorkStatus.Succeeded
                          && evaluation.TotalCount > 0
                          && evaluation.ScoredCount == evaluation.TotalCount
                          && evaluation.PassedCount == entries.Count(entry => entry.Passed)
                          && entries.Count == evaluation.TotalCount
                          && resultIds.Count == entries.Count
                          && resultIds.SetEquals(membership.HoldoutSampleIds);
        if (!fullyScored)
        {
            throw new EvaluationRejectedException(
                $"The {side} evaluation must have successfully scored every frozen hold-out sample before it can be compared.");
        }
    }

    private static TrainingEvaluationMembershipV1 ReadMembership(TrainingEvaluationRecord evaluation, string side)
    {
        try
        {
            return JsonSerializer.Deserialize<TrainingEvaluationMembershipV1>(evaluation.MembershipJson.Span, TrainingJson.Options)
                   ?? throw new EvaluationRejectedException(UnreadableMembership(side));
        }
        catch (JsonException)
        {
            // An unreadable membership cannot be shown to match the other side, and a comparison that cannot prove its
            // own precondition must refuse rather than assume it.
            throw new EvaluationRejectedException(UnreadableMembership(side));
        }
    }

    private static string UnreadableMembership(string side) =>
        $"The {side} evaluation's frozen hold-out membership could not be read, so the two sides cannot be shown to be comparable.";

    /// <summary>Which half of the lineage is missing, if either — the two ways a suggestion cannot be completed.</summary>
    private static string? UnavailableReason(string? baseModelName, string? tunedModelName)
    {
        if (baseModelName is null)
        {
            return "This run was not started from an installed model, so its base cannot be evaluated; the accuracy comparison is unavailable.";
        }

        return tunedModelName is null
            ? "No completed staged GGUF exists for this run yet, so there is no tuned artifact to evaluate."
            : null;
    }

    /// <summary>
    ///     The whole comparison, as a pure function of what is persisted. Kept static and dependency-free so the
    ///     reproducibility test can rerun it against records read back from storage.
    /// </summary>
    internal static TrainingComparisonDeltasV1 ComputeDeltas(TrainingEvaluationRecord baseEvaluation,
        TrainingEvaluationRecord tunedEvaluation,
        BenchmarkRunRecord? baseBenchmark,
        BenchmarkRunRecord? tunedBenchmark)
    {
        ArgumentNullException.ThrowIfNull(baseEvaluation);
        ArgumentNullException.ThrowIfNull(tunedEvaluation);

        var baseEntries = TrainingEvaluationResults.Read(baseEvaluation.ResultsJson);
        var tunedEntries = TrainingEvaluationResults.Read(tunedEvaluation.ResultsJson);
        var baseTally = TrainingEvaluationResults.Tally(baseEntries);
        var tunedTally = TrainingEvaluationResults.Tally(tunedEntries);

        var basePassed = baseEntries.Count(entry => entry.Passed);
        var tunedPassed = tunedEntries.Count(entry => entry.Passed);
        var baseAccuracy = Accuracy(basePassed, baseEntries.Count);
        var tunedAccuracy = Accuracy(tunedPassed, tunedEntries.Count);
        var available = baseEntries.Count > 0 && tunedEntries.Count > 0;

        var perKind = baseTally.Keys.Union(tunedTally.Keys, StringComparer.Ordinal)
                               .OrderBy(kind => kind, StringComparer.Ordinal)
                               .Select(kind =>
                               {
                                   var left = baseTally.TryGetValue(kind, out var found) ? found : new TrainingEvaluationKindTally(0, 0);
                                   var right = tunedTally.TryGetValue(kind, out var other) ? other : new TrainingEvaluationKindTally(0, 0);
                                   var leftAccuracy = Accuracy(left.Passed, left.Total);
                                   var rightAccuracy = Accuracy(right.Passed, right.Total);
                                   return new ComparisonKindDeltaV1(kind, left.Total, left.Passed, right.Total, right.Passed,
                                       leftAccuracy, rightAccuracy, rightAccuracy - leftAccuracy);
                               })
                               .ToArray();

        return new TrainingComparisonDeltasV1
        {
            BaseModelName = baseEvaluation.ModelName,
            TunedModelName = tunedEvaluation.ModelName,
            BaseScoredCount = baseEntries.Count,
            BasePassedCount = basePassed,
            TunedScoredCount = tunedEntries.Count,
            TunedPassedCount = tunedPassed,
            BaseAccuracy = baseAccuracy,
            TunedAccuracy = tunedAccuracy,
            AccuracyDelta = tunedAccuracy - baseAccuracy,
            PerKind = perKind,
            AccuracyAvailable = available,
            UnavailableReason = available ? null : "One of the two evaluations scored no samples.",
            Benchmark = ComputeBenchmarkDelta(baseBenchmark, tunedBenchmark)
        };
    }

    private static ComparisonBenchmarkDeltaV1? ComputeBenchmarkDelta(BenchmarkRunRecord? baseRun, BenchmarkRunRecord? tunedRun)
    {
        if (baseRun is null && tunedRun is null)
        {
            return null;
        }

        var baseJudge = ReadJudgeScore(baseRun);
        var tunedJudge = ReadJudgeScore(tunedRun);
        return new ComparisonBenchmarkDeltaV1
        {
            BaseTokensPerSecond = baseRun?.TokensPerSecond,
            TunedTokensPerSecond = tunedRun?.TokensPerSecond,
            TokensPerSecondDelta = Subtract(tunedRun?.TokensPerSecond, baseRun?.TokensPerSecond),
            BaseDurationMs = baseRun?.DurationMs,
            TunedDurationMs = tunedRun?.DurationMs,
            BaseUserScore = baseRun?.UserScore,
            TunedUserScore = tunedRun?.UserScore,
            UserScoreDelta = Subtract(tunedRun?.UserScore, baseRun?.UserScore),
            BaseJudgeScore = baseJudge,
            TunedJudgeScore = tunedJudge,
            JudgeScoreDelta = Subtract(tunedJudge, baseJudge)
        };
    }

    /// <summary>
    ///     The run's judge score, read from its current attempt. Plaintext and already 0..100 — the report no longer
    ///     decrypts and parses a verdict to find a number the store can sort by.
    /// </summary>
    private static int? ReadJudgeScore(BenchmarkRunRecord? run) =>
        run?.Judge?.Score;

    private static double Accuracy(int passed, int total) =>
        total == 0 ? 0d : (double)passed / total;

    private static double? Subtract(double? left, double? right) =>
        left is { } a && right is { } b ? a - b : null;

    private static int? Subtract(int? left, int? right) =>
        left is { } a && right is { } b ? a - b : null;

    private async Task<BenchmarkRunRecord?> RequireBenchmarkAsync(Guid? runId, CancellationToken cancellationToken)
    {
        if (runId is not { } id)
        {
            return null;
        }

        return await _benchmarks.GetRunAsync(id, cancellationToken).ConfigureAwait(false)
               ?? throw new EvaluationRejectedException("The paired benchmark run was not found.");
    }

    private static bool Matches(string modelName, string? candidate) =>
        !string.IsNullOrWhiteSpace(candidate) && string.Equals(modelName, candidate, StringComparison.Ordinal);
}
