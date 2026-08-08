namespace XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;

/// <summary>
///     A directed handoff edge: the agent keyed <see cref="FromKey" /> may hand the conversation off to the agent
///     keyed <see cref="ToKey" />. <see cref="Reason" /> is the optional routing hint; when null MAF derives the
///     reason from the target participant's Description (or Name).
/// </summary>
public sealed record OrchestrationEdge
{
    public required string FromKey { get; init; }

    public required string ToKey { get; init; }

    public string? Reason { get; init; }
}
