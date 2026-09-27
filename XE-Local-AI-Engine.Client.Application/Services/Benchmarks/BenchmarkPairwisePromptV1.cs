namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

public static class BenchmarkPairwisePromptV1
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    /// <summary>The instruction every pairwise judging gets.</summary>
    /// <remarks>
    ///     The length-neutrality sentence is carried over verbatim from the pointwise prompt: it is the
    ///     best-documented judge bias and the only cheap counter to it. Same rule as the pointwise prompt — this text
    ///     is hashed NOWHERE, so any wording change MUST bump
    ///     <see cref="BenchmarkJudgePolicyVersions.PairwisePromptVersion" />, or verdicts taken either side of the
    ///     edit share a cohort and are fitted against each other as though they answered the same question.
    /// </remarks>
    public const string SystemPrompt =
        "Two answers to the SAME benchmark task are supplied. Judge which one better satisfies the task. "
        + "Do not reward length or verbosity for its own sake, and do not prefer an answer for being shown first. "
        + "Answer \"tie\" only when you genuinely cannot separate them. "
        + "Return exactly one JSON object matching the supplied output schema. Return no markdown and no extra properties.";

    /// <summary>
    ///     Appended when either side had to be cut to fit its half of the judge window. Appended rather than folded in
    ///     unconditionally so a comparison of two complete answers stays byte-identical to every other one.
    /// </summary>
    public const string TruncatedAnswerInstruction =
        " At least one answer was cut off to fit the judge context; the cut is marked in the text. "
        + "Judge what is present and do not credit work an answer does not contain.";

    public static string SystemPromptFor(bool anyAnswerTruncated) =>
        anyAnswerTruncated ? SystemPrompt + TruncatedAnswerInstruction : SystemPrompt;

    /// <summary>
    ///     Frames the caller's already-shaped pieces verbatim. Both answers are the GRADED projection
    ///     (<see cref="BenchmarkOutputParts.ForJudge" />) of their run's transcript, each bounded to HALF the judge
    ///     window because both have to fit beside the task, the reference answer and the verdict.
    /// </summary>
    /// <param name="firstAnswerPartsJson">The answer shown as <c>a</c> — which run that is depends on the swap order.</param>
    public static string BuildUserPayloadJson(string taskJson,
        string? referenceAnswer,
        string firstAnswerPartsJson,
        string secondAnswerPartsJson,
        string outputSchemaJson,
        bool firstAnswerTruncated,
        bool secondAnswerTruncated)
    {
        var payload = new JsonObject
        {
            ["task"] = JsonNode.Parse(taskJson),
            ["referenceAnswer"] = referenceAnswer is null ? null : JsonValue.Create(referenceAnswer),
            ["answerA"] = JsonNode.Parse(firstAnswerPartsJson),
            ["answerB"] = JsonNode.Parse(secondAnswerPartsJson),
            ["outputSchema"] = JsonNode.Parse(outputSchemaJson)
        };
        if (firstAnswerTruncated)
        {
            payload["answerATruncated"] = true;
        }

        if (secondAnswerTruncated)
        {
            payload["answerBTruncated"] = true;
        }

        return payload.ToJsonString(PayloadOptions);
    }
}
