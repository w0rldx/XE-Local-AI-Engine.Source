namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Decisions;

/// <summary>What a decision provider concluded. <see cref="Choice" /> is null when the answer named no label at all.</summary>
/// <remarks>
///     <see cref="Confidence" /> and <see cref="Probabilities" /> are null from the <c>llm</c> provider: a
///     grammar-constrained answer carries no calibrated score, and a fabricated one would route on noise.
/// </remarks>
internal sealed record DecisionResult
{
    public required string? Choice { get; init; }

    public required double? Confidence { get; init; }

    public required IReadOnlyDictionary<string, double>? Probabilities { get; init; }
}
