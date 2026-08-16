namespace XE_Local_AI_Engine.Client.Services.Agents;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Compiles a <c>Kind=Orchestrator</c> agent definition + its <c>OrchestrationTopologyJson</c> into the loopback
///     orchestration spec carried on the runtime package (orchestration). A sibling of <see cref="IAgentDefinitionResolver" />
///     so the single-agent resolver stays untouched (regression-safe). Returns an <see cref="OrchestrationResolution" />
///     whose <see cref="OrchestrationResolution.Orchestration" /> is <c>null</c> — signalling the caller to
///     degrade to the single-agent path (the orchestrator runs as a lone agent on its own prompt + tools) — when:
///     the definition is not an orchestrator; the topology is empty/invalid; the effective model is not tool-capable;
///     the triage participant is missing/deleted; or fewer than two capable participants survive. Every degrade EXCEPT
///     "not an orchestrator" carries a typed <see cref="OrchestrationDegradationReason" /> so the caller can surface a
///     turn notice instead of degrading silently (the operator otherwise only sees a server log). The encrypted/server
///     path never calls this; orchestration is loopback-only.
/// </summary>
public interface IOrchestrationResolver
{
    /// <summary>
    ///     Resolves the orchestration spec for an orchestrator definition, or a degraded resolution (no spec + the typed
    ///     reason) telling the caller to run the turn single-agent.
    /// </summary>
    /// <param name="orchestrator">The conversation's bound definition (must be <c>Kind=Orchestrator</c> to resolve).</param>
    /// <param name="activeModelId">The model the turn runs on when the orchestrator pins none; gates capability.</param>
    /// <param name="retrievalQuery">
    ///     The incoming user-turn text used to relevance-gate each participant's playbook injection (relevance retrieval),
    ///     applied with the SAME threshold/top-k/re-order decision as the single-agent path. Blank (or at/below the
    ///     threshold) keeps the full static prepend per participant, so each participant's composed prompt stays
    ///     byte-identical to the pre-retrieval path.
    /// </param>
    /// <param name="supportsTools">
    ///     Whether the active model advertises the Ollama <c>tools</c> capability. When <c>false</c> every participant's
    ///     tool offer is withheld (the model cannot drive tool calls), independent of the existing per-tool
    ///     <c>ToolCapableModels</c> name allow-list. Defaults to <c>true</c> so callers that do not gate keep today's behaviour.
    /// </param>
    /// <remarks>
    ///     Provider locality for the knowledge-tool gate is resolved PER PARTICIPANT from each participant's own
    ///     effective (post-pin) model, not from the turn's active model, so a cloud-pinned participant is withheld the
    ///     knowledge tools even when the active model is local. There is therefore no turn-level cloud flag here.
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
///     The outcome of one orchestration resolve: the compiled <see cref="ResolvedOrchestration" />, or <c>null</c> plus
///     the typed <see cref="Reason" /> the turn degraded to single-agent. <see cref="ReasonText" /> is a sanitized,
///     user-facing phrase (never a path, id, exception, or prompt) and <see cref="DegradationNotice" /> composes the one
///     sentence BOTH the send and the regenerate path emit, so the two cannot drift.
/// </summary>
public sealed record OrchestrationResolution(ResolvedOrchestration? Orchestration, OrchestrationDegradationReason Reason, string? ReasonText)
{
    /// <summary>The definition is not an orchestrator (or there is no bound definition): no spec, and nothing to report.</summary>
    public static OrchestrationResolution NotOrchestrated { get; } = new(Orchestration: null, OrchestrationDegradationReason.None, ReasonText: null);

    /// <summary>The turn runs as a single agent and the operator should be told why.</summary>
    public static OrchestrationResolution Degraded(OrchestrationDegradationReason reason, string reasonText)
    {
        return new OrchestrationResolution(Orchestration: null, reason, reasonText);
    }

    /// <summary>The orchestration compiled; the caller carries the spec on the runtime package.</summary>
    public static OrchestrationResolution Compiled(ResolvedOrchestration orchestration)
    {
        return new OrchestrationResolution(orchestration, OrchestrationDegradationReason.None, ReasonText: null);
    }

    /// <summary>
    ///     The sanitized turn-notice sentence for a degraded resolve, or <c>null</c> when there is nothing to report (the
    ///     orchestration compiled, or the definition was never an orchestrator — a single-kind agent must stay silent).
    /// </summary>
    public string? DegradationNotice => ReasonText is null
        ? null
        : $"Orchestration was not used for this turn: {ReasonText}. The agent ran as a single agent instead.";
}

/// <summary>
///     The resolved orchestration: the compiled <see cref="OrchestrationSpec" /> (carried on the runtime package and
///     folded into the config hash) plus the orchestrator's own resolved single-agent inputs, so the caller still
///     populates the package's system prompt / model / version / reasoning from the orchestrator definition (the spec
///     rides ALONGSIDE the existing single-agent fields, never replaces them — a runner that ignores the spec still
///     runs a valid single-agent turn).
/// </summary>
/// <param name="AnyParticipantIsCloud">
///     True when ANY resolved participant's effective model is cloud-hosted. The orchestration seed is a SINGLE shared
///     list broadcast to every participant (MAF has no per-participant seed), and per-participant TOOL stripping cannot
///     redact content already embedded in that seed. So the caller must gate node-local private data (conversation
///     attachments) on this aggregate — not only the orchestrator's own model locality — or an attachment inlined into
///     the shared seed would reach a cloud participant. <see cref="FirstCloudParticipantModel" /> names one such model.
/// </param>
/// <param name="FirstCloudParticipantModel">
///     The effective model id of the first cloud participant (ordinal by definition id, for determinism), or
///     <see langword="null" /> when no participant is cloud. Used to name the cloud model in the attachments-withheld notice.
/// </param>
public sealed record ResolvedOrchestration(
    OrchestrationSpec Spec,
    string ResolvedSystemPrompt,
    string? ModelProfile,
    string? ReasoningEffort,
    int AgentDefinitionVersion,
    bool AnyParticipantIsCloud,
    string? FirstCloudParticipantModel);
