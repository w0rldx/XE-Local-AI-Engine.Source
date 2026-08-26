namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

/// <summary>One pairwise judging's parsed output, in the PRESENTATION order the judge was shown.</summary>
public sealed record BenchmarkPairwiseResultV1(int SchemaVersion, string Verdict, string Rationale);

/// <summary>
///     The pairwise verdict schema, in the same two shapes the pointwise one ships in: the bounded copy that goes into
///     the prompt so the model is told the limits, and the bound-free copy handed to constrained decoding, because
///     llama.cpp compiles a response format into GBNF and its sampler initialization breaks on length bounds.
/// </summary>
public static class BenchmarkPairwiseOutputSchemaV1
{
    public const int MaximumRationaleLength = 2048;

    public const string Json =
        "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"schemaVersion\",\"verdict\",\"rationale\"],\"properties\":{"
        + "\"schemaVersion\":{\"const\":1},"
        + "\"verdict\":{\"type\":\"string\",\"enum\":[\"a\",\"b\",\"tie\"]},"
        + "\"rationale\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":2048}}}";

    /// <summary>The bound-free copy handed to constrained decoding — see the type summary.</summary>
    public const string ResponseFormatJson =
        "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"schemaVersion\",\"verdict\",\"rationale\"],\"properties\":{"
        + "\"schemaVersion\":{\"const\":1},"
        + "\"verdict\":{\"type\":\"string\",\"enum\":[\"a\",\"b\",\"tie\"]},"
        + "\"rationale\":{\"type\":\"string\"}}}";
}

public static class BenchmarkPairwisePromptV1
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    /// <summary>
    ///     The instruction every pairwise judging gets. The length-neutrality sentence is carried over verbatim from
    ///     the pointwise prompt: it is the best-documented judge bias and the only cheap counter to it.
    ///     <para>
    ///         Same rule as the pointwise prompt: this text is hashed NOWHERE. Any wording change must bump
    ///         <see cref="BenchmarkJudgePolicyVersions.PairwisePromptVersion" />, or verdicts taken either side of the
    ///         edit share a cohort and are fitted against each other as though they answered the same question.
    ///     </para>
    /// </summary>
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

/// <summary>
///     Fail-closed parse of one pairwise verdict. Anything outside the schema — a missing member, an unknown verdict
///     token, a rationale past the bound — fails the comparison rather than being coerced into a verdict, because a
///     coerced verdict is a vote nobody cast.
/// </summary>
public static class BenchmarkPairwiseResultParser
{
    public static BenchmarkPairwiseResultV1 Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new BenchmarkExecutionException("The pairwise judge returned no output.");
        }

        try
        {
            using var document = JsonDocument.Parse(ExtractObject(content));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var version)
                || !version.TryGetInt32(out var schemaVersion)
                || schemaVersion != 1
                || !root.TryGetProperty("verdict", out var verdictElement)
                || verdictElement.GetString() is not { } verdict
                || verdict is not (BenchmarkBradleyTerry.VerdictA or BenchmarkBradleyTerry.VerdictB or BenchmarkBradleyTerry.VerdictTie))
            {
                throw new JsonException();
            }

            var rationale = root.TryGetProperty("rationale", out var rationaleElement) ? rationaleElement.GetString()?.Trim() : null;
            return rationale is null || rationale.Length == 0 || rationale.Length > BenchmarkPairwiseOutputSchemaV1.MaximumRationaleLength
                ? throw new JsonException()
                : new BenchmarkPairwiseResultV1(schemaVersion, verdict, rationale);
        }
        catch (JsonException)
        {
            throw new BenchmarkExecutionException("The pairwise judge output did not match the required schema.");
        }
    }

    /// <summary>
    ///     The outermost JSON object in the response. Constrained decoding makes this the whole string in practice; a
    ///     model that wrapped it in prose still has its object read rather than the whole judging thrown away.
    /// </summary>
    private static string ExtractObject(string content)
    {
        var start = content.IndexOf('{', StringComparison.Ordinal);
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : content;
    }

    /// <summary>
    ///     Turns a verdict about the PRESENTATION order into one about the canonical pair. With
    ///     <paramref name="order" /> 1 the runs were shown swapped, so a verdict of <c>a</c> means the canonical B won.
    ///     This is the whole bookkeeping of the position swap, and it lives in exactly one place.
    /// </summary>
    public static string ToCanonicalVerdict(string verdict, int order)
    {
        if (order == 0 || string.Equals(verdict, BenchmarkBradleyTerry.VerdictTie, StringComparison.Ordinal))
        {
            return verdict;
        }

        return string.Equals(verdict, BenchmarkBradleyTerry.VerdictA, StringComparison.Ordinal)
            ? BenchmarkBradleyTerry.VerdictB
            : BenchmarkBradleyTerry.VerdictA;
    }
}
