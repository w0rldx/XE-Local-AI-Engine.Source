namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json.Serialization;

public sealed record BenchmarkJudgeCriterionScoreV2(string Id, int Score, string Rationale);

/// <summary>One verifiable criterion's server-side evidence, stored so a verified score is auditable after the fact.</summary>
public sealed record BenchmarkJudgeVerifierResultV1(string Id, string Kind, bool Passed, string Detail);

/// <summary>
///     One judging's stored result: the per-criterion scores, the summary, the weighted score, the judge model's
///     content fingerprint, and the verifier evidence when there was any.
/// </summary>
/// <remarks>
///     <paramref name="Verifiers" /> is written only when present, so a legacy stored result that carries no verifier
///     evidence round-trips unchanged.
/// </remarks>
/// <param name="Verifiers">
///     The evidence behind every non-<c>llm</c> criterion, or <see langword="null" /> for a judging that had none.
/// </param>
public sealed record BenchmarkJudgeResultV2(
    int SchemaVersion,
    IReadOnlyList<BenchmarkJudgeCriterionScoreV2> Criteria,
    string Summary,
    int Score,
    string JudgeModelContentFingerprint,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<BenchmarkJudgeVerifierResultV1>? Verifiers = null);

/// <summary>The judge's output schema in two shapes.</summary>
/// <remarks>
/// <see cref="Json"/> is the documentation copy embedded in the prompt: bounded, so the model is TOLD the limits
/// <see cref="BenchmarkJudgeResultParser"/> enforces. <see cref="ResponseFormatJson"/> is the same schema with every
/// <c>minLength</c>/<c>maxLength</c>/<c>minItems</c>/<c>maxItems</c> removed, and is the one handed to constrained
/// decoding: llama.cpp compiles a response-format schema into a GBNF grammar, and length bounds break its sampler
/// initialization. Dropping them from the grammar costs nothing — the parser still rejects anything outside them.
/// </remarks>
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
