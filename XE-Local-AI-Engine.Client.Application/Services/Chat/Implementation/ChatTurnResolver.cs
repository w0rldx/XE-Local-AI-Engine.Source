namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     Shared per-turn resolution for the local send and regenerate paths. It resolves the active model's advertised
///     capabilities, the effective agent definition, and any compiled orchestration spec — the tail both
///     <see cref="NodeChatStreamService" /> and <see cref="NodeChatRegenerationService" /> perform identically after
///     each derives its own model/agent/retrieval-query head. Capabilities come from the shared
///     <see cref="IModelCapabilityResolver" />, so both paths gate on the same provider-routing decision the
///     orchestration path uses.
/// </summary>
public sealed class ChatTurnResolver(
    IAgentDefinitionResolver agentDefinitionResolver,
    IAgentDefinitionStore agentDefinitionStore,
    IOrchestrationResolver orchestrationResolver,
    IModelCapabilityResolver modelCapabilityResolver,
    ILogger<ChatTurnResolver> logger)
{
    /// <summary>
    ///     Resolves the effective per-turn agent (definition + orchestration) plus advertised capabilities from a
    ///     caller-derived head (the active model, whether that model was an explicit user pick, the effective agent id,
    ///     and the relevance-retrieval query). The effective model is the resolved pin when honored, otherwise the
    ///     active model — the single source of truth both the runtime package and the persisted attribution stamp from.
    /// </summary>
    internal async Task<ChatTurnResolution> ResolveAsync(string? activeModel,
        bool requiresInstalledChatModel,
        Guid? effectiveAgentId,
        string? retrievalQuery,
        bool userPickedConcreteModel,
        CancellationToken cancellationToken)
    {
        // Resolve the active model's advertised capabilities ONCE so the think field and the tool offer are both gated
        // by what the model can actually do. A non-thinking model returns HTTP 400 for any think value; a non-tools
        // model cannot drive tool calls. Unknown/offline capabilities resolve to NOT-capable (the safe default) so a
        // plain chat still works without tripping the 400. The same pass classifies provider LOCALITY (Codex / Azure
        // Foundry = cloud), so the knowledge-tool provider-locality gate reuses this per-turn resolution instead of
        // adding its own hot-path lookup.
        // The per-stage timings measure resolution cost: Debug-level + timestamp-based, so it costs a long on the hot
        // path when Debug logging is off. The same stages are wrapped in coarse spans so a send stalled here shows up
        // in exported traces. No high-cardinality attributes: the spans carry no model names, prompts, or ids.
        var resolveStartTimestamp = Stopwatch.GetTimestamp();
        using var resolveActivity = NodeActivitySource.Source.StartActivity("chat.turn.resolve");

        var capabilitiesStart = Stopwatch.GetTimestamp();
        ModelCapabilitySnapshot capabilities;
        using (NodeActivitySource.Source.StartActivity("chat.turn.resolve_capabilities"))
        {
            capabilities = await modelCapabilityResolver.ResolveAsync(activeModel, cancellationToken).ConfigureAwait(false);
        }

        var supportsThinking = capabilities.SupportsThinking;
        var supportsTools = capabilities.SupportsTools;
        var supportsVision = capabilities.SupportsVision;
        var activeModelIsCloud = capabilities.IsCloud;
        var capabilitiesMs = Stopwatch.GetElapsedTime(capabilitiesStart).TotalMilliseconds;

        // Resolve the effective agent definition. A null result — no effective id (the seed is missing), or an id whose
        // definition was deleted — keeps the default persona: the embedded system prompt, the full capability-gated
        // offer, and agent version 1. When resolved, the definition supplies the system prompt, the tool offer (full for
        // the Default Assistant, intersected otherwise), the pinned model profile, the reasoning effort, the version
        // that feeds the config hash, AND the attribution snapshot (id + display name). The retrieval query is the
        // relevance-retrieval query (inert below the threshold / unbound, so the prompt stays byte-identical).
        var agentStart = Stopwatch.GetTimestamp();
        ResolvedAgentRuntime? resolved;
        using (NodeActivitySource.Source.StartActivity("chat.turn.resolve_agent"))
        {
            resolved = await agentDefinitionResolver.ResolveAsync(effectiveAgentId, activeModel, retrievalQuery, supportsTools, honorModelProfile: !userPickedConcreteModel, activeModelIsCloud,
                                                        cancellationToken)
                                                    .ConfigureAwait(false);
        }

        var agentMs = Stopwatch.GetElapsedTime(agentStart).TotalMilliseconds;

        // When the effective definition is a tool-capable orchestrator, resolve a compiled orchestration spec to carry
        // on the package — the runner branches to the handoff workflow. A null result (not an orchestrator, an
        // empty/invalid topology, an incapable model, or too few capable participants) leaves the package single-agent,
        // keeping the unbound/single-agent path byte-identical. The same retrieval query drives per-participant retrieval.
        // The already-resolved runtime is threaded in so its Kind gates the reload.
        var orchestrationStart = Stopwatch.GetTimestamp();
        OrchestrationResolution orchestration;
        using (NodeActivitySource.Source.StartActivity("chat.turn.resolve_orchestration"))
        {
            orchestration = await ResolveOrchestrationAsync(effectiveAgentId, resolved, activeModel, retrievalQuery, supportsTools, cancellationToken).ConfigureAwait(false);
        }

        var orchestrationMs = Stopwatch.GetElapsedTime(orchestrationStart).TotalMilliseconds;

        // The single source of truth for the model that actually runs this turn: the resolved pin when honored (the
        // resolver returned null for ModelProfile when the user's explicit pick suppressed it), otherwise the active
        // model. Both the runtime package AND the persisted assistant-message attribution are stamped from this so the
        // label can never disagree with what ran.
        var effectiveModel = resolved?.ModelProfile ?? activeModel;

        // The EFFECTIVE model's provider locality (used to gate node-local private-data exposure — knowledge/file tools
        // and conversation attachments). The resolver classified the pinned effective model; with no bound agent (or no
        // pin) the effective model IS the active model, so reuse the active-model flag.
        var effectiveModelIsCloud = resolved?.EffectiveModelIsCloud ?? activeModelIsCloud;

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "Chat-turn resolution completed in {ElapsedMs:F2} ms (capabilities={CapabilitiesMs:F2} ms, agent={AgentMs:F2} ms, orchestration={OrchestrationMs:F2} ms; boundAgent={HasBoundAgent}, orchestration={HasOrchestration}).",
                Stopwatch.GetElapsedTime(resolveStartTimestamp).TotalMilliseconds,
                capabilitiesMs,
                agentMs,
                orchestrationMs,
                effectiveAgentId is not null,
                orchestration.Orchestration is not null);
        }

        return new ChatTurnResolution(activeModel, effectiveModel, resolved, orchestration, supportsThinking, supportsTools, supportsVision, requiresInstalledChatModel, activeModelIsCloud,
            effectiveModelIsCloud);
    }

    /// <summary>
    ///     Resolves a compiled orchestration spec for a bound orchestrator definition (orchestration), or a degraded
    ///     resolution that runs the turn single-agent. Only a bound conversation triggers the extra record fetch; an
    ///     unbound conversation or a non-orchestrator definition returns
    ///     <see cref="OrchestrationResolution.NotOrchestrated" /> without resolving, so the single-agent path is
    ///     byte-identical AND no degradation notice is raised for an agent that never asked for orchestration.
    /// </summary>
    private async Task<OrchestrationResolution> ResolveOrchestrationAsync(Guid? agentDefinitionId,
        ResolvedAgentRuntime? resolved,
        string? activeModel,
        string? retrievalQuery,
        bool supportsTools,
        CancellationToken cancellationToken)
    {
        if (agentDefinitionId is not { } definitionId)
        {
            return OrchestrationResolution.NotOrchestrated;
        }

        // The resolver already loaded (and AES-GCM-decrypted) this definition on the line above; reuse its Kind
        // instead of a SECOND uncached GetByIdAsync + decrypt. A null resolution (no binding / deleted definition) or a
        // non-orchestrator Kind — the overwhelmingly common path, incl. every mode-off Default Assistant send — means
        // there is no orchestration to compile, so the reload is skipped entirely. Only a bound orchestrator (rare) pays
        // the reload to obtain the full definition the compiler needs.
        if (resolved is not { Kind: AgentDefinitionKind.Orchestrator })
        {
            return OrchestrationResolution.NotOrchestrated;
        }

        var definition = await agentDefinitionStore.GetByIdAsync(definitionId, cancellationToken).ConfigureAwait(false);
        if (definition is null || definition.Kind != AgentDefinitionKind.Orchestrator)
        {
            return OrchestrationResolution.NotOrchestrated;
        }

        // Orchestration resolves each participant's knowledge-tool locality from its own effective model internally, so
        // no turn-level cloud flag is threaded here.
        return await orchestrationResolver.ResolveAsync(definition, activeModel, retrievalQuery, supportsTools, cancellationToken).ConfigureAwait(false);
    }
}
