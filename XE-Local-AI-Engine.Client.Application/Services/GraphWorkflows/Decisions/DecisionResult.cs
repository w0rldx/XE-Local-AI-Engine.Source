namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Decisions;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>What a decision provider concluded. <see cref="Choice" /> is null when the answer named no label at all.</summary>
/// <remarks>
///     <see cref="Confidence" /> and <see cref="Probabilities" /> are null from the <c>llm</c> provider: a
///     grammar-constrained answer carries no score, and a fabricated one would route on noise. A native provider reports
///     the model's own numbers, which upstream does not calibrate. A native provider that could not answer sets
///     <see cref="FailureClass" /> and <see cref="FailureReason" /> instead of throwing.
/// </remarks>
internal sealed record DecisionResult
{
    public required string? Choice { get; init; }

    public required double? Confidence { get; init; }

    public required IReadOnlyDictionary<string, double>? Probabilities { get; init; }

    /// <summary>The question type the provider answered, which the executor holds to the node's own.</summary>
    public string Type { get; init; } = GraphWorkflowGraph.DecisionChoice;

    public double? Noul { get; init; }

    public double? Score { get; init; }

    public IReadOnlyDictionary<string, string>? Legend { get; init; }

    public int? InputTokens { get; init; }

    public long? DurationMs { get; init; }

    public GraphWorkflowFailureClass FailureClass { get; init; } = GraphWorkflowFailureClass.None;

    /// <summary>The operator-facing reason when <see cref="FailureClass" /> is set; never model output.</summary>
    public string? FailureReason { get; init; }
}
