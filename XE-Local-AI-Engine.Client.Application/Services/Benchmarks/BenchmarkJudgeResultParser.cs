namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json;

/// <summary>
/// Strict, fail-closed parse of the judge's reply. Anything the schema does not describe exactly is a failed judging, not
/// a salvaged score.
/// </summary>
public static class BenchmarkJudgeResultParser
{
    private const string InvalidResultMessage = "The benchmark judge returned an invalid result.";

    public static BenchmarkJudgeResultV2 Parse(string rawJson, BenchmarkJudgeRubricV1 rubric, string judgeModelContentFingerprint)
    {
        ArgumentNullException.ThrowIfNull(rubric);
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException();
            }

            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 3
                || !properties.Select(static property => property.Name).ToHashSet(StringComparer.Ordinal)
                              .SetEquals(["schemaVersion", "criteria", "summary"])
                || !TryReadInt32(root.GetProperty("schemaVersion"), out var schemaVersion)
                || schemaVersion != BenchmarkJudgePolicyVersions.OutputSchemaVersion)
            {
                throw new JsonException();
            }

            var summary = ReadBoundedText(root.GetProperty("summary"), BenchmarkJudgeOutputSchemaV2.MaximumSummaryLength);
            var criteria = ReadCriteria(root.GetProperty("criteria"), rubric);
            return new BenchmarkJudgeResultV2(BenchmarkJudgePolicyVersions.OutputSchemaVersion,
                criteria,
                summary,
                BenchmarkJudgeScoreCalculator.Compute(rubric, criteria),
                judgeModelContentFingerprint);
        }
        catch (JsonException exception)
        {
            throw new BenchmarkExecutionException(InvalidResultMessage, exception)
            {
                Source = exception.Source
            };
        }
    }

    private static IReadOnlyList<BenchmarkJudgeCriterionScoreV2> ReadCriteria(JsonElement element, BenchmarkJudgeRubricV1 rubric)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != rubric.Criteria.Count)
        {
            throw new JsonException();
        }

        var expected = rubric.Criteria.Select(static criterion => criterion.Id).ToHashSet(StringComparer.Ordinal);
        var scores = new List<BenchmarkJudgeCriterionScoreV2>(rubric.Criteria.Count);
        var seen = new HashSet<string>(rubric.Criteria.Count, StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException();
            }

            var properties = item.EnumerateObject().ToArray();
            if (properties.Length != 3
                || !properties.Select(static property => property.Name).ToHashSet(StringComparer.Ordinal)
                              .SetEquals(["id", "score", "rationale"]))
            {
                throw new JsonException();
            }

            var idElement = item.GetProperty("id");
            if (idElement.ValueKind != JsonValueKind.String
                || idElement.GetString() is not { } id
                || !expected.Contains(id)
                || !seen.Add(id)
                || !TryReadInt32(item.GetProperty("score"), out var score)
                || score is < BenchmarkJudgeOutputSchemaV2.MinimumCriterionScore or > BenchmarkJudgeOutputSchemaV2.MaximumCriterionScore)
            {
                throw new JsonException();
            }

            scores.Add(new BenchmarkJudgeCriterionScoreV2(id,
                score,
                ReadBoundedText(item.GetProperty("rationale"), BenchmarkJudgeOutputSchemaV2.MaximumRationaleLength)));
        }

        return scores;
    }

    private static bool TryReadInt32(JsonElement element, out int value)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            value = 0;
            return false;
        }

        return element.TryGetInt32(out value);
    }

    private static string ReadBoundedText(JsonElement element, int maximumLength)
    {
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } value)
        {
            throw new JsonException();
        }

        var trimmed = value.Trim();
        return trimmed.Length is 0 || trimmed.Length > maximumLength ? throw new JsonException() : trimmed;
    }
}
