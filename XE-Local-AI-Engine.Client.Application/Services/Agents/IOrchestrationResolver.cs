namespace XE_Local_AI_Engine.Client.Services.Agents;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Compiles a <c>Kind=Orchestrator</c> agent definition + its <c>OrchestrationTopologyJson</c> into the loopback
///     orchestration spec carried on the runtime package (orchestration). A sibling of <see cref="IAgentDefinitionResolver" />
///     so the single-agent resolver stays untouched (regression-safe). Returns <c>null</c> — signalling the caller to
///     degrade to the single-agent path (the orchestrator runs as a lone agent on its own prompt + tools) — when:
///     the definition is not an orchestrator; the topology is empty/invalid; the effective model is not tool-capable;
///     the triage participant is missing/deleted; or fewer than two capable participants survive. The encrypted/server
///     path never calls this; orchestration is loopback-only.
/// </summary>
public interface IOrchestrationResolver
{
    /// <summary>
    ///     Resolves the orchestration spec for an orchestrator definition, or <c>null</c> to degrade to single-agent.
    /// </summary>
    /// <param name="orchestrator">The conversation's bound definition (must be <c>Kind=Orchestrator</c> to resolve).</param>
    /// <param name="activeModelId">The model the turn runs on when the orchestrator pins none; gates capability.</param>
    /// <param name="retrievalQuery">
    ///     The incoming user-turn text used to relevance-gate each participant's playbook injection (relevance retrieval and cohort monitoring, plan
    ///     §4.2), applied with the SAME threshold/top-k/re-order decision as the single-agent path. Blank (or at/below the
    ///     threshold) keeps the full static prepend per participant, so each participant's composed prompt stays
    ///     byte-identical to the pre-retrieval path.
    /// </param>
    /// <param name="supportsTools">
    ///     Whether the active model advertises the Ollama <c>tools</c> capability. When <c>false</c> every participant's
    ///     tool offer is withheld (the model cannot drive tool calls), independent of the existing per-tool
    ///     <c>ToolCapableModels</c> name allow-list. Defaults to <c>true</c> so callers that do not gate keep today's behaviour.
    /// </param>
    Task<ResolvedOrchestration?> ResolveAsync(AgentDefinitionRecord orchestrator, string? activeModelId, string? retrievalQuery = null, bool supportsTools = true, CancellationToken cancellationToken = default);
}

/// <summary>
///     The resolved orchestration: the compiled <see cref="OrchestrationSpec" /> (carried on the runtime package and
///     folded into the config hash) plus the orchestrator's own resolved single-agent inputs, so the caller still
///     populates the package's system prompt / model / version / reasoning from the orchestrator definition (the spec
///     rides ALONGSIDE the existing single-agent fields, never replaces them — a runner that ignores the spec still
///     runs a valid single-agent turn).
/// </summary>
public sealed record ResolvedOrchestration(
    OrchestrationSpec Spec,
    string ResolvedSystemPrompt,
    string? ModelProfile,
    string? ReasoningEffort,
    int AgentDefinitionVersion);
