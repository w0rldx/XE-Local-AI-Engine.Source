namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>The up-front per-turn resolution shared by placeholder/variant stamping and runtime-package construction.</summary>
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
    bool ReasoningBudgetEnforceable = true)
{
    /// <summary>
    ///     The compiled orchestration spec, or <see langword="null" /> when the turn runs single-agent (the definition is
    ///     not an orchestrator, or its orchestration degraded — see
    ///     <see cref="OrchestrationResolution.Reason" />/<see cref="OrchestrationResolution.DegradationNotice" />).
    /// </summary>
    public ResolvedOrchestration? Orchestration => OrchestrationOutcome.Orchestration;
}
