namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>The up-front per-turn resolution shared by placeholder/variant stamping and runtime-package construction.</summary>
/// <param name="AllowAutoModelSwap">
///     Whether the runner's reasoning-effort dispatcher may replace the effective model for this turn. Named for the
///     PERMISSION rather than the state, so the <see langword="false" /> default is by construction "the model is
///     pinned, never swap it" — unknown provenance can only fail closed. It is <see langword="true" /> on ONE turn
///     shape: no explicit user pick AND no honored agent pin, i.e. the node's default model was chosen for this turn
///     and nobody asked for a specific one. Carried because the provenance lives only here — the runtime package
///     retains the EFFECTIVE model and not how it was chosen.
/// </param>
internal sealed record ChatTurnResolution(
    string? ActiveModel,
    string? EffectiveModel,
    ResolvedAgentRuntime? Resolved,
    OrchestrationResolution OrchestrationOutcome,
    bool SupportsThinking,
    bool SupportsTools,
    bool SupportsVision,
    bool RequiresInstalledChatModel,
    bool ActiveModelIsCloud,
    bool EffectiveModelIsCloud,
    bool ReasoningBudgetEnforceable = true,
    bool AllowAutoModelSwap = false)
{
    /// <summary>
    ///     The compiled orchestration spec, or <see langword="null" /> when the turn runs single-agent (the definition is
    ///     not an orchestrator, or its orchestration degraded — see
    ///     <see cref="OrchestrationResolution.Reason" />/<see cref="OrchestrationResolution.DegradationNotice" />).
    /// </summary>
    public ResolvedOrchestration? Orchestration => OrchestrationOutcome.Orchestration;
}
