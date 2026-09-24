namespace XE_Local_AI_Engine.Client.Services.Agents;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Compiles an orchestrator definition and its <c>OrchestrationTopologyJson</c> into the loopback orchestration
///     spec carried on the runtime package.
/// </summary>
/// <remarks>
///     A sibling of <see cref="IAgentDefinitionResolver" />, so the single-agent resolver stays untouched. A null
///     <see cref="OrchestrationResolution.Orchestration" /> tells the caller to run the orchestrator as a lone agent:
///     the definition is not an orchestrator, the topology is empty or invalid, the effective model cannot call
///     tools, the triage participant is gone, or fewer than two capable participants survive. Every degrade but the
///     first carries a typed reason, so nothing degrades silently. Orchestration is loopback-only.
/// </remarks>
public interface IOrchestrationResolver
{
    /// <summary>
    ///     Resolves the orchestration spec for an orchestrator definition, or a degraded resolution (no spec + the typed
    ///     reason) telling the caller to run the turn single-agent.
    /// </summary>
    /// <param name="orchestrator">The conversation's bound definition (must be <c>Kind=Orchestrator</c> to resolve).</param>
    /// <param name="activeModelId">The model the turn runs on when the orchestrator pins none; gates capability.</param>
    /// <param name="retrievalQuery">Relevance-gates each participant's playbook injection, as on the single-agent path.</param>
    /// <param name="supportsTools">Whether the model advertises <c>tools</c>; false withholds every participant's offer.</param>
    /// <remarks>
    ///     Provider locality for the knowledge-tool gate is resolved PER PARTICIPANT from each one's own effective,
    ///     post-pin model rather than the turn's active model, so a cloud-pinned participant is withheld the knowledge
    ///     tools even on a local active model. There is therefore no turn-level cloud flag here.
    /// </remarks>
    Task<OrchestrationResolution> ResolveAsync(AgentDefinitionRecord orchestrator, string? activeModelId, string? retrievalQuery = null, bool supportsTools = true,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Why a <c>Kind=Orchestrator</c> definition did NOT compile into an orchestration for this turn. Every value except
///     <see cref="None" /> is an operator-visible degradation: the turn still runs, but as a single agent.
/// </summary>
public enum OrchestrationDegradationReason
{
    /// <summary>No degradation: the orchestration compiled, or the definition is not an orchestrator to begin with.</summary>
    None = 0,

    /// <summary>The definition's <c>OrchestrationTopologyJson</c> is missing, empty, or does not parse.</summary>
    TopologyInvalid = 1,

    /// <summary>The orchestrator's effective model does not advertise (or is not allow-listed for) tool calling.</summary>
    ModelNotToolCapable = 2,

    /// <summary>The topology's triage participant is missing, deleted, or was dropped as not tool-capable.</summary>
    TriageMissing = 3,

    /// <summary>Fewer than the two capable participants handoff routing needs survived resolution.</summary>
    TooFewCapableParticipants = 4
}

/// <summary>
///     The outcome of one orchestration resolve: the compiled <see cref="ResolvedOrchestration" />, or <c>null</c>
///     plus the typed <see cref="Reason" /> the turn degraded to single-agent on.
/// </summary>
/// <remarks>
///     <see cref="ReasonText" /> is a sanitized, user-facing phrase, never a path, id, exception or prompt, and
///     <see cref="DegradationNotice" /> composes the one sentence BOTH the send and the regenerate path emit, so the
///     two cannot drift.
/// </remarks>
public sealed class OrchestrationResolution
{
    public required ResolvedOrchestration? Orchestration { get; init; }

    public required OrchestrationDegradationReason Reason { get; init; }

    public required string? ReasonText { get; init; }

    /// <summary>The definition is not an orchestrator (or there is no bound definition): no spec, and nothing to report.</summary>
    public static OrchestrationResolution NotOrchestrated { get; } = new()
    {
        Orchestration = null,
        Reason = OrchestrationDegradationReason.None,
        ReasonText = null
    };

    /// <summary>The turn runs as a single agent and the operator should be told why.</summary>
    public static OrchestrationResolution Degraded(OrchestrationDegradationReason reason, string reasonText)
    {
        return new OrchestrationResolution
        {
            Orchestration = null,
            Reason = reason,
            ReasonText = reasonText
        };
    }

    /// <summary>The orchestration compiled; the caller carries the spec on the runtime package.</summary>
    public static OrchestrationResolution Compiled(ResolvedOrchestration orchestration)
    {
        return new OrchestrationResolution
        {
            Orchestration = orchestration,
            Reason = OrchestrationDegradationReason.None,
            ReasonText = null
        };
    }

    /// <summary>
    ///     The sanitized turn-notice sentence for a degraded resolve, or <c>null</c> when there is nothing to report (the
    ///     orchestration compiled, or the definition was never an orchestrator — a single-kind agent must stay silent).
    /// </summary>
    public string? DegradationNotice =>
        ReasonText is null
            ? null
            : $"Orchestration was not used for this turn: {ReasonText}. The agent ran as a single agent instead.";
}

/// <summary>
///     The resolved orchestration: the compiled <see cref="OrchestrationSpec" />, folded into the config hash, plus
///     the orchestrator's own resolved single-agent inputs.
/// </summary>
/// <remarks>
///     The caller still populates the package's system prompt, model, version and reasoning from the orchestrator
///     definition: the spec rides ALONGSIDE the single-agent fields and never replaces them, so a runner that ignores
///     it still runs a valid single-agent turn.
/// </remarks>
public sealed class ResolvedOrchestration
{
    public required OrchestrationSpec Spec { get; init; }

    public required string ResolvedSystemPrompt { get; init; }

    public required string? ModelProfile { get; init; }

    public required string? ReasoningEffort { get; init; }

    public required int AgentDefinitionVersion { get; init; }

    /// <summary>
    ///     True when ANY resolved participant's effective model is cloud-hosted.
    /// </summary>
    /// <remarks>
    ///     The orchestration seed is a SINGLE shared list broadcast to every participant, and per-participant tool
    ///     stripping cannot redact content already embedded in it. The caller must therefore gate node-local private
    ///     data on this aggregate, not on the orchestrator's own locality, or an inlined attachment reaches a cloud
    ///     participant. <see cref="FirstCloudParticipantModel" /> names one such model.
    /// </remarks>
    public required bool AnyParticipantIsCloud { get; init; }

    /// <summary>
    ///     The effective model id of the first cloud participant (ordinal by definition id, for determinism), or
    ///     <see langword="null" /> when no participant is cloud. Used to name the cloud model in the attachments-withheld notice.
    /// </summary>
    public required string? FirstCloudParticipantModel { get; init; }
}
