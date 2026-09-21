namespace XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;

using Microsoft.Extensions.AI;

/// <summary>One agent in a handoff orchestration, MAF-agnostic.</summary>
/// <remarks>
///     Built by the resolver from a participant <c>AgentDefinition</c> through the same tool-offer contract the
///     single-agent path uses: <see cref="Tools" /> is the already-projected, capability-gated offer list rendered as
///     bridged <see cref="AITool" />s — ApiSide tools as real bridges, ClientLocal tools as name-only placeholders the
///     orchestration factory swaps for registry executables before the agent runs. The factory never sees a transport
///     DTO; it reuses the single-agent <c>InvocationToolResolver</c> verbatim.
/// </remarks>
public sealed record OrchestrationParticipant
{
    /// <summary>Stable participant key — the participant <c>AgentDefinition.Id</c> as a string.</summary>
    /// <remarks>
    ///     Used to map edges, and to correlate the auto-generated <c>AIAgent.Id</c> back to a participant for the
    ///     streaming UX.
    /// </remarks>
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
    ///     Defaults to <see langword="true" />.
    /// </summary>
    /// <remarks>
    ///     Gates how <see cref="ReasoningEffort" /> is translated onto the participant agent's construction-time
    ///     <c>ChatOptions</c> (see <see cref="ParticipantReasoningOptions" />), mirroring the single-agent think
    ///     contract: a model without the capability returns HTTP 400 for any <c>think</c> field. Cloud providers ignore
    ///     the unknown property, so the default never suppresses a capable model's reasoning.
    /// </remarks>
    public bool SupportsThinking { get; init; } = true;

    /// <summary>
    ///     Whether llama-server can ENFORCE a per-request <c>reasoning_budget_tokens</c> for this participant's
    ///     resolved <see cref="ModelId" />, its chat template rendering a literal reasoning end marker. Defaults to
    ///     <see langword="true" />.
    /// </summary>
    /// <remarks>
    ///     When <see langword="false" /> the participant's construction-time options carry NO budget marker (see
    ///     <see cref="ParticipantReasoningOptions" />), because llama.cpp would accept the field and then ignore it.
    ///     The default never removes a working cap.
    /// </remarks>
    public bool ReasoningBudgetEnforceable { get; init; } = true;

    /// <summary>
    ///     The participant's projected, approval-flagged offer list as bridged tools (see the type remarks). Empty
    ///     when the participant offers no tools.
    /// </summary>
    public required IReadOnlyList<AITool> Tools { get; init; }

    /// <summary>
    ///     The prior turns this participant is seeded with; the system prompt is built from
    ///     <see cref="Instructions" /> separately.
    /// </summary>
    /// <remarks>
    ///     Usually shared across participants for the first turn; the workflow carries history across hops thereafter.
    /// </remarks>
    public IReadOnlyList<ChatMessage> ConversationContext { get; init; } = [];

    /// <summary>
    ///     The effective per-slot context window, in tokens, the participant's resolved <see cref="ModelId" /> was
    ///     launched with, when known.
    /// </summary>
    /// <remarks>
    ///     Carried onto the participant agent's construction-time <c>ChatOptions</c> as <c>num_ctx</c>, so
    ///     <c>ProviderCallBudgetChatClient</c> sizes THIS participant against its own launched window rather than the
    ///     shared default. Workflow participants never receive the outer runner's per-turn <c>RunOptions</c>, so like
    ///     reasoning it must be baked in at construction. <see langword="null" /> — not yet resident, or cloud/Ollama —
    ///     leaves the inner budgeter on its configured default window.
    /// </remarks>
    public int? EffectiveContextTokens { get; init; }
}
