namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>One pairwise judging's parsed output, in the PRESENTATION order the judge was shown.</summary>
public sealed class BenchmarkPairwiseResultV1
{
    public required int SchemaVersion { get; init; }

    public required string Verdict { get; init; }

    public required string Rationale { get; init; }
}

/// <summary>The pairwise verdict schema, in the same two shapes the pointwise one ships in.</summary>
/// <remarks>
///     The bounded copy goes into the prompt so the model is told the limits; the bound-free copy is handed to
///     constrained decoding, because llama.cpp compiles a response format into GBNF and its sampler initialization
///     breaks on length bounds.
/// </remarks>
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
