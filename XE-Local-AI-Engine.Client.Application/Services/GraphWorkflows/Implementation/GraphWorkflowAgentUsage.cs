namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

/// <summary>
///     What one agent turn cost, as the node run's output document reports it. Every member is nullable because the
///     runner reports what its provider gave it and no provider reports all of them.
/// </summary>
internal sealed class GraphWorkflowAgentUsage
{
    public required int? InputTokens { get; init; }

    public required int? OutputTokens { get; init; }

    public required int? TotalTokens { get; init; }

    public required int? ReasoningTokens { get; init; }

    public required long? DurationMs { get; init; }

    public required string? FinishReason { get; init; }

    public required string? Model { get; init; }
}
