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
    ///     Whether the runner's reasoning-effort dispatcher may replace the effective model for this turn.
    /// </summary>
    /// <remarks>
    ///     Named for the PERMISSION rather than the state, so the <see langword="false" /> default reads "pinned,
    ///     never swap" and unknown provenance can only fail closed. It is <see langword="true" /> on ONE turn shape:
    ///     no explicit user pick AND no honored agent pin. It is carried here because the provenance lives nowhere
    ///     else — the runtime package retains the EFFECTIVE model, not how it was chosen.
    /// </remarks>
    public bool AllowAutoModelSwap { get; init; }

    /// <summary>
    ///     The compiled orchestration spec, or <see langword="null" /> when the turn runs single-agent (the definition is
    ///     not an orchestrator, or its orchestration degraded — see
    ///     <see cref="OrchestrationResolution.Reason" />/<see cref="OrchestrationResolution.DegradationNotice" />).
    /// </summary>
    public ResolvedOrchestration? Orchestration => OrchestrationOutcome.Orchestration;
}
