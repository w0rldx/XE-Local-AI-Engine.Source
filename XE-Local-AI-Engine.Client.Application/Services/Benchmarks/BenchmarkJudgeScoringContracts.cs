namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

public sealed record BenchmarkJudgeCriterionScoreV2(string Id, int Score, string Rationale);

public sealed record BenchmarkJudgeResultV2(
    int SchemaVersion,
    IReadOnlyList<BenchmarkJudgeCriterionScoreV2> Criteria,
    string Summary,
    int Score,
    string JudgeModelContentFingerprint);

/// <summary>
/// The judge's output schema in two shapes. <see cref="Json"/> is the documentation copy embedded in the prompt: bounded,
/// so the model is TOLD the limits <see cref="BenchmarkJudgeResultParser"/> enforces. <see cref="ResponseFormatJson"/> is
/// the same schema with every <c>minLength</c>/<c>maxLength</c>/<c>minItems</c>/<c>maxItems</c> removed, and is the one
/// handed to constrained decoding: llama.cpp compiles a response-format schema into a GBNF grammar, and length bounds
/// break its sampler initialization. Dropping them from the grammar costs nothing — the parser still rejects anything
/// outside the bounds.
/// </summary>
public static class BenchmarkJudgeOutputSchemaV2
{
    public const int MinimumCriterionScore = 0;
    public const int MaximumCriterionScore = 10;
    public const int MaximumRationaleLength = 2048;
    public const int MaximumSummaryLength = 4096;

    public const string Json =
        "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"schemaVersion\",\"criteria\",\"summary\"],\"properties\":{"
        + "\"schemaVersion\":{\"const\":2},"
        + "\"criteria\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":8,\"items\":{\"type\":\"object\",\"additionalProperties\":false,"
        + "\"required\":[\"id\",\"score\",\"rationale\"],\"properties\":{"
        + "\"id\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":32},"
        + "\"score\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":10},"
        + "\"rationale\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":2048}}}},"
        + "\"summary\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":4096}}}";

    /// <summary>The bound-free copy of <see cref="Json"/> handed to constrained decoding — see the type summary.</summary>
    public const string ResponseFormatJson =
        "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"schemaVersion\",\"criteria\",\"summary\"],\"properties\":{"
        + "\"schemaVersion\":{\"const\":2},"
        + "\"criteria\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,"
        + "\"required\":[\"id\",\"score\",\"rationale\"],\"properties\":{"
        + "\"id\":{\"type\":\"string\"},"
        + "\"score\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":10},"
        + "\"rationale\":{\"type\":\"string\"}}}},"
        + "\"summary\":{\"type\":\"string\"}}}";
}

/// <summary>
/// Weighted rubric score in 0..100, integer arithmetic only, rounded half away from zero. All inputs are non-negative,
/// so <c>(2·num + den) / (2·den)</c> is that rounding; the maximum numerator (8 × 100 × 10 × 10 = 80 000) leaves room in
/// an <see cref="int"/> for the doubling.
/// </summary>
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
            throw new BenchmarkExecutionException(InvalidResultMessage)
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

public static class BenchmarkJudgePromptV2
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    public const string SystemPrompt =
        "Evaluate only the supplied benchmark task and primary output against the supplied rubric. "
        + "Score every rubric criterion from 0 to 10 and give a short rationale for each score. "
        + "Return exactly one JSON object matching the supplied output schema. Return no markdown and no extra properties.";

    /// <summary>
    ///     Embeds the caller's already-shaped pieces verbatim — this builder frames, it never re-serializes.
    ///     <paramref name="primaryOutputPartsJson" /> must be the GRADED projection of the run's transcript
    ///     (<see cref="BenchmarkOutputParts.ForJudge" />): coalesced, with reasoning parts removed, because hidden
    ///     chain-of-thought is not the graded answer and a thinking model's raw transcript does not fit the judge window.
    ///     The payload's property names and order are unchanged by that narrowing, so neither
    ///     <see cref="BenchmarkJudgePolicyVersions.PromptVersion" /> nor the output-schema version moves.
    /// </summary>
    public static string BuildUserPayloadJson(string taskJson,
        string? referenceAnswer,
        BenchmarkJudgeRubricV1 rubric,
        string primaryOutputPartsJson,
        string outputSchemaJson)
    {
        ArgumentNullException.ThrowIfNull(rubric);
        var payload = new JsonObject
        {
            ["task"] = JsonNode.Parse(taskJson)
        };
        if (referenceAnswer is not null)
        {
            payload["referenceAnswer"] = JsonValue.Create(referenceAnswer);
        }

        payload["rubric"] = JsonSerializer.SerializeToNode(rubric, PayloadOptions);
        payload["primaryOutputParts"] = JsonNode.Parse(primaryOutputPartsJson);
        payload["outputSchema"] = JsonNode.Parse(outputSchemaJson);
        return payload.ToJsonString(PayloadOptions);
    }
}
