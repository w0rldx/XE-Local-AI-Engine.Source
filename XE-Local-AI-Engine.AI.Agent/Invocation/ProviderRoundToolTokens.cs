namespace XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>One sent tool: its model-facing name and the estimated tokens of its definition.</summary>
public sealed record ProviderRoundToolTokens
{
    public required string Name { get; init; }

    public required int Tokens { get; init; }
}
