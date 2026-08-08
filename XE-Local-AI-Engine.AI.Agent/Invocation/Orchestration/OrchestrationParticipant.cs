namespace XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;

using Microsoft.Extensions.AI;

/// <summary>
///     One agent in a handoff orchestration, MAF-agnostic. Built by the resolver from a participant
///     <c>AgentDefinition</c> projected through the same tool-offer contract the single-agent path uses: the
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
    ///     Whether this participant's resolved <see cref="ModelId" /> advertises the Ollama <c>thinking</c> capability.
    ///     Gates how <see cref="ReasoningEffort" /> is translated onto the participant agent's construction-time
    ///     <c>ChatOptions</c> (see <see cref="ParticipantReasoningOptions" />), mirroring the single-agent
    ///     think contract: a model without the capability returns HTTP 400 for any <c>think</c> field. Defaults to
    ///     <see langword="true" /> — cloud providers ignore the unknown property, so <see langword="true" /> is the safe
    ///     default that never suppresses a capable model's reasoning.
    /// </summary>
    public bool SupportsThinking { get; init; } = true;

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

    /// <summary>
    ///     ORC-07: the effective per-slot context window (in tokens) the participant's resolved <see cref="ModelId" />
    ///     was launched with, when known. Carried onto the participant agent's construction-time <c>ChatOptions</c> as
    ///     the <c>num_ctx</c> option so the innermost provider-round budgeter (<c>ProviderCallBudgetChatClient</c>) sizes
    ///     THIS participant against its own launched window rather than the shared configured default. Workflow
    ///     participants never receive the outer runner's per-turn <c>RunOptions</c>, so — like reasoning — this must be
    ///     baked in at construction. <see langword="null" /> (unknown: the model is not yet resident, or is cloud/Ollama)
    ///     leaves the inner budgeter on its configured default window for this participant.
    /// </summary>
    public int? EffectiveContextTokens { get; init; }
}
