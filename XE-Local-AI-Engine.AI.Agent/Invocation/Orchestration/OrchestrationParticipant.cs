namespace XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;

using Microsoft.Extensions.AI;

/// <summary>
///     One agent in a handoff orchestration, MAF-agnostic. Built by the resolver (Lane B) from a participant
///     <c>AgentDefinition</c> projected through the same P3 contract the single-agent path uses: the
///     <see cref="Tools" /> list is the already-projected, capability-gated offer list rendered as bridged
///     <see cref="AITool" />s (ApiSide tools as real bridges, ClientLocal tools as name-only offer placeholders the
///     orchestration factory swaps for registry executables — Option A/B/C — before the agent runs). The factory
///     never sees a transport DTO; it reuses the single-agent <c>InvocationToolResolver</c> verbatim.
/// </summary>
public sealed record OrchestrationParticipant
{
    /// <summary>
    ///     Stable participant key — the participant <c>AgentDefinition.Id</c> as a string. Used to map edges and to
    ///     correlate the auto-generated <c>AIAgent.Id</c> back to a participant for streaming UX.
    /// </summary>
    public required string Key { get; init; }

    public required string Name { get; init; }

    /// <summary>
    ///     The participant's description. MAF derives a handoff target's routing reason from the target agent's
    ///     Description (or Name) when an edge supplies no explicit reason, so a good description drives good routing.
    /// </summary>
    public string? Description { get; init; }

    public required string Instructions { get; init; }

    public required string ModelId { get; init; }

    public string? ReasoningEffort { get; init; }

    /// <summary>
    ///     The participant's projected, approval-flagged offer list as bridged tools (see the type remarks). Empty
    ///     when the participant offers no tools.
    /// </summary>
    public required IReadOnlyList<AITool> Tools { get; init; }

    /// <summary>
    ///     The conversation history this participant should be seeded with (system prompt is built from
    ///     <see cref="Instructions" />; these are the prior turns). Usually shared across participants for the first
    ///     turn; the workflow carries history across hops thereafter.
    /// </summary>
    public IReadOnlyList<ChatMessage> ConversationContext { get; init; } = [];
}
