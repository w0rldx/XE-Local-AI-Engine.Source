namespace XE_Local_AI_Engine.Client.Services.Training.Comparison;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;

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

        var baseBenchmark = await RequireBenchmarkAsync(command.BaseBenchmarkRunId, cancellationToken).ConfigureAwait(false);
        var tunedBenchmark = await RequireBenchmarkAsync(command.TunedBenchmarkRunId, cancellationToken).ConfigureAwait(false);

        var deltas = ComputeDeltas(baseEvaluation, tunedEvaluation, baseBenchmark, tunedBenchmark);
        return await _evaluations.CreateComparisonAsync(new TrainingComparisonInput(command.Name.Trim(),
                                         baseEvaluation.Id,
                                         tunedEvaluation.Id,
                                         JsonSerializer.SerializeToUtf8Bytes(deltas, TrainingJson.Options),
                                         command.BaseBenchmarkRunId,
                                         command.TunedBenchmarkRunId,
                                         command.TrainingRunId ?? tunedEvaluation.TrainingRunId ?? baseEvaluation.TrainingRunId),
                                     cancellationToken)
                                 .ConfigureAwait(false);
    }

    public Task<IReadOnlyList<TrainingComparisonRecord>> ListAsync(CancellationToken cancellationToken = default) =>
        _evaluations.ListComparisonsAsync(cancellationToken);

    public Task<TrainingComparisonRecord?> GetAsync(Guid comparisonId, CancellationToken cancellationToken = default) =>
        _evaluations.GetComparisonAsync(comparisonId, cancellationToken);

    public Task DeleteAsync(Guid comparisonId, long expectedVersion, CancellationToken cancellationToken = default) =>
        _evaluations.DeleteComparisonAsync(comparisonId, expectedVersion, cancellationToken);

    public async Task<ComparisonSuggestion> SuggestAsync(Guid trainingRunId, CancellationToken cancellationToken = default)
    {
        var run = await _runs.GetAsync(trainingRunId, cancellationToken).ConfigureAwait(false)
                  ?? throw new EvaluationRejectedException("The training run was not found.");

        var artifacts = await _runs.ListArtifactsAsync(trainingRunId, cancellationToken).ConfigureAwait(false);
        var tunedModelName = artifacts.Select(artifact => artifact.CommittedModelName)
                                      .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        var existing = await _evaluations.ListAsync(trainingRunId, cancellationToken).ConfigureAwait(false);

        // Matched by model name rather than by a stored "side" flag: the side an evaluation is on IS which model it
        // scored, and one stored flag that disagreed with the model name would be a second truth to keep in sync.
        var baseEvaluation = existing.FirstOrDefault(item => Matches(item.ModelName, run.LinkedInstalledModelName));
        var tunedEvaluation = existing.FirstOrDefault(item => Matches(item.ModelName, tunedModelName));

        var reason = UnavailableReason(run.LinkedInstalledModelName, tunedModelName);
        return new ComparisonSuggestion(trainingRunId,
            run.LinkedInstalledModelName,
            tunedModelName,
            baseEvaluation?.Id,
            tunedEvaluation?.Id,
            reason);
    }

    /// <summary>Which half of the lineage is missing, if either — the two ways a suggestion cannot be completed.</summary>
    private static string? UnavailableReason(string? baseModelName, string? tunedModelName)
    {
        if (baseModelName is null)
        {
            return "This run was not started from an installed model, so its base cannot be evaluated; the accuracy comparison is unavailable.";
        }

        return tunedModelName is null
            ? "No artifact from this run has been promoted to the registry yet, so there is no tuned model to evaluate."
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

    private static int? ReadJudgeScore(BenchmarkRunRecord? run)
    {
        if (run?.JudgeResultJson is not { } payload || payload.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BenchmarkJudgeResultV1>(payload.Span, TrainingJson.Options)?.Score;
        }
        catch (JsonException)
        {
            // A judge verdict this node can no longer read leaves the rest of the report intact.
            return null;
        }
    }

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
