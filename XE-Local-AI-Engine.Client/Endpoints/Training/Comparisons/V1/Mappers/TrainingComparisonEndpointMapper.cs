namespace XE_Local_AI_Engine.Client.Endpoints.Training.Comparisons.V1.Mappers;

using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Comparison;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;

internal static class TrainingComparisonEndpointMapper
{
    public static ComparisonResponse ToResponse(this TrainingComparisonRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new ComparisonResponse
        {
            Id = record.Id,
            Name = record.Name,
            BaseEvaluationRunId = record.BaseEvaluationRunId,
            TunedEvaluationRunId = record.TunedEvaluationRunId,
            BaseBenchmarkRunId = record.BaseBenchmarkRunId,
            TunedBenchmarkRunId = record.TunedBenchmarkRunId,
            TrainingRunId = record.TrainingRunId,
            // Null rather than a fabricated zero report when the stored document cannot be read: a comparison that
            // shows 0% both sides is a lie, an absent one is a refresh.
            Deltas = TrainingEndpointSupport.Read<TrainingComparisonDeltasV1>(record.DeltasJson)?.ToResponse(),
            Version = record.Version,
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc
        };
    }

    public static ComparisonSuggestionResponse ToResponse(this ComparisonSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        return new ComparisonSuggestionResponse
        {
            TrainingRunId = suggestion.TrainingRunId,
            BaseModelName = suggestion.BaseModelName,
            TunedModelName = suggestion.TunedModelName,
            BaseEvaluationRunId = suggestion.BaseEvaluationRunId,
            TunedEvaluationRunId = suggestion.TunedEvaluationRunId,
            UnavailableReason = suggestion.UnavailableReason
        };
    }

    private static ComparisonDeltasResponse ToResponse(this TrainingComparisonDeltasV1 deltas) =>
        new()
        {
            BaseModelName = deltas.BaseModelName,
            TunedModelName = deltas.TunedModelName,
            BaseScoredCount = deltas.BaseScoredCount,
            BasePassedCount = deltas.BasePassedCount,
            TunedScoredCount = deltas.TunedScoredCount,
            TunedPassedCount = deltas.TunedPassedCount,
            BaseAccuracy = deltas.BaseAccuracy,
            TunedAccuracy = deltas.TunedAccuracy,
            AccuracyDelta = deltas.AccuracyDelta,
            PerKind = deltas.PerKind.Select(kind => new ComparisonKindDeltaResponse
                            {
                                Kind = kind.Kind,
                                BaseTotal = kind.BaseTotal,
                                BasePassed = kind.BasePassed,
                                TunedTotal = kind.TunedTotal,
                                TunedPassed = kind.TunedPassed,
                                BaseAccuracy = kind.BaseAccuracy,
                                TunedAccuracy = kind.TunedAccuracy,
                                AccuracyDelta = kind.AccuracyDelta
                            })
                            .ToArray(),
            AccuracyAvailable = deltas.AccuracyAvailable,
            UnavailableReason = deltas.UnavailableReason,
            Benchmark = deltas.Benchmark is not { } benchmark
                ? null
                : new ComparisonBenchmarkDeltaResponse
                {
                    BaseTokensPerSecond = benchmark.BaseTokensPerSecond,
                    TunedTokensPerSecond = benchmark.TunedTokensPerSecond,
                    TokensPerSecondDelta = benchmark.TokensPerSecondDelta,
                    BaseDurationMs = benchmark.BaseDurationMs,
                    TunedDurationMs = benchmark.TunedDurationMs,
                    BaseUserScore = benchmark.BaseUserScore,
                    TunedUserScore = benchmark.TunedUserScore,
                    UserScoreDelta = benchmark.UserScoreDelta,
                    BaseJudgeScore = benchmark.BaseJudgeScore,
                    TunedJudgeScore = benchmark.TunedJudgeScore,
                    JudgeScoreDelta = benchmark.JudgeScoreDelta
                }
        };
}
