namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>The up-front per-turn resolution shared by placeholder/variant stamping and runtime-package construction.</summary>
internal sealed class ChatTurnResolution
{
    public required string? ActiveModel { get; init; }

    public required string? EffectiveModel { get; init; }

    public required ResolvedAgentRuntime? Resolved { get; init; }

    public required OrchestrationResolution OrchestrationOutcome { get; init; }

    public required bool SupportsThinking { get; init; }

    public required bool SupportsTools { get; init; }

    public required bool SupportsVision { get; init; }

    public required bool RequiresInstalledChatModel { get; init; }

    public required bool ActiveModelIsCloud { get; init; }

    public required bool EffectiveModelIsCloud { get; init; }

    public bool ReasoningBudgetEnforceable { get; init; } = true;

    /// <summary>
    ///     Whether the runner's reasoning-effort dispatcher may replace the effective model for this turn. Named for the
    ///     PERMISSION rather than the state, so the <see langword="false" /> default is by construction "the model is
    ///     pinned, never swap it" — unknown provenance can only fail closed. It is <see langword="true" /> on ONE turn
    ///     shape: no explicit user pick AND no honored agent pin, i.e. the node's default model was chosen for this turn
    ///     and nobody asked for a specific one. Carried because the provenance lives only here — the runtime package
    ///     retains the EFFECTIVE model and not how it was chosen.
    /// </summary>
    public bool AllowAutoModelSwap { get; init; }

    /// <summary>
    ///     The compiled orchestration spec, or <see langword="null" /> when the turn runs single-agent (the definition is
    ///     not an orchestrator, or its orchestration degraded — see
    ///     <see cref="OrchestrationResolution.Reason" />/<see cref="OrchestrationResolution.DegradationNotice" />).
    /// </summary>
    public ResolvedOrchestration? Orchestration => OrchestrationOutcome.Orchestration;
}
