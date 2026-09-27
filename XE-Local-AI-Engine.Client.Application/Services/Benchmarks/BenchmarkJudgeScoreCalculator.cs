namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>Weighted rubric score in 0..100, integer arithmetic only, rounded half away from zero.</summary>
/// <remarks>
/// All inputs are non-negative, so <c>(2·num + den) / (2·den)</c> is that rounding; the maximum numerator
/// (8 × 100 × 10 × 10 = 80 000) leaves room in an <see cref="int"/> for the doubling.
/// </remarks>
public static class BenchmarkJudgeScoreCalculator
{
    public static int Compute(BenchmarkJudgeRubricV1 rubric, IReadOnlyList<BenchmarkJudgeCriterionScoreV2> scores)
    {
        ArgumentNullException.ThrowIfNull(rubric);
        ArgumentNullException.ThrowIfNull(scores);
        BenchmarkJudgePolicyValidator.ValidateRubric(rubric);
        if (scores.Count != rubric.Criteria.Count)
        {
            throw new BenchmarkExecutionException("The judge scores do not cover the rubric criteria.");
        }

        var byId = new Dictionary<string, BenchmarkJudgeCriterionScoreV2>(scores.Count, StringComparer.Ordinal);
        foreach (var score in scores)
        {
            byId[score.Id] = score;
        }

        if (byId.Count != scores.Count)
        {
            throw new BenchmarkExecutionException("The judge scores repeat a rubric criterion.");
        }

        var numerator = 0;
        var denominator = 0;
        foreach (var criterion in rubric.Criteria)
        {
            if (!byId.TryGetValue(criterion.Id, out var score))
            {
                throw new BenchmarkExecutionException("The judge scores do not cover the rubric criteria.");
            }

            if (score.Score is < BenchmarkJudgeOutputSchemaV2.MinimumCriterionScore or > BenchmarkJudgeOutputSchemaV2.MaximumCriterionScore)
            {
                throw new BenchmarkExecutionException("A judge criterion score is out of range.");
            }

            numerator += criterion.Weight * score.Score * 10;
            denominator += criterion.Weight;
        }

        return ((2 * numerator) + denominator) / (2 * denominator);
    }
}
