namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     Shared per-turn resolution for the local send and regenerate paths: the active model's advertised
///     capabilities, the effective agent definition and any compiled orchestration spec.
/// </summary>
/// <remarks>
///     This is the tail <see cref="NodeChatStreamService" /> and <see cref="NodeChatRegenerationService" /> perform
///     identically after each derives its own model, agent and retrieval-query head. Capabilities come from the
///     shared <see cref="IModelCapabilityResolver" />, so both gate on the orchestration path's routing decision.
/// </remarks>
public sealed class ChatTurnResolver
{
    private readonly IAgentDefinitionResolver _agentDefinitionResolver;
    private readonly IAgentDefinitionStore _agentDefinitionStore;
    private readonly IOrchestrationResolver _orchestrationResolver;
    private readonly IModelCapabilityResolver _modelCapabilityResolver;
    private readonly ILogger<ChatTurnResolver> _logger;

    public ChatTurnResolver(
        IAgentDefinitionResolver agentDefinitionResolver,
        IAgentDefinitionStore agentDefinitionStore,
        IOrchestrationResolver orchestrationResolver,
        IModelCapabilityResolver modelCapabilityResolver,
        ILogger<ChatTurnResolver> logger)
    {
        _agentDefinitionResolver = agentDefinitionResolver;
        _agentDefinitionStore = agentDefinitionStore;
        _orchestrationResolver = orchestrationResolver;
        _modelCapabilityResolver = modelCapabilityResolver;
        _logger = logger;
    }

    /// <summary>
    ///     Resolves the effective per-turn agent, its orchestration and the advertised capabilities from a
    ///     caller-derived head.
    /// </summary>
    /// <remarks>
    ///     The head is the active model, whether it was an explicit user pick, the effective agent id and the
    ///     relevance-retrieval query. The effective model is the resolved pin when honored, otherwise the active
    ///     model: the single source of truth both the runtime package and the persisted attribution stamp from.
    /// </remarks>
    internal async Task<ChatTurnResolution> ResolveAsync(string? activeModel,
        bool requiresInstalledChatModel,
        Guid? effectiveAgentId,
        string? retrievalQuery,
        bool userPickedConcreteModel,
        CancellationToken cancellationToken)
    {
        // Capabilities resolve ONCE, with unknown meaning NOT-capable so a plain chat works instead of tripping a
        // provider 400, and the same pass classifies the provider LOCALITY the egress gates reuse.
        var resolveStartTimestamp = Stopwatch.GetTimestamp();
        using var resolveActivity = NodeActivitySource.Source.StartActivity("chat.turn.resolve");

        // Capabilities must describe the model that ACTUALLY runs the turn: with no installed GGUF the active model is
        // null while a pin still supplies one, so on that branch the PIN is the head or the tool offer is stripped.
        var capabilityModel = activeModel;
        if (activeModel is null && !userPickedConcreteModel && effectiveAgentId is { } pinnedDefinitionId)
        {
            var pinnedDefinition = await _agentDefinitionStore.GetByIdAsync(pinnedDefinitionId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(pinnedDefinition?.ModelProfile))
            {
                capabilityModel = pinnedDefinition.ModelProfile;
            }
        }

        var capabilitiesStart = Stopwatch.GetTimestamp();
        ModelCapabilitySnapshot capabilities;
        using (NodeActivitySource.Source.StartActivity("chat.turn.resolve_capabilities"))
        {
            capabilities = await _modelCapabilityResolver.ResolveAsync(capabilityModel, cancellationToken);
        }

        var supportsThinking = capabilities.SupportsThinking;
        var supportsTools = capabilities.SupportsTools;
        var supportsVision = capabilities.SupportsVision;
        var activeModelIsCloud = capabilities.IsCloud;
        var capabilitiesMs = Stopwatch.GetElapsedTime(capabilitiesStart).TotalMilliseconds;

        // Resolve the effective agent definition. A null result — a missing seed or a deleted definition — keeps the
        // default persona, prompt, full offer and version 1; a resolved one supplies those plus pin, effort and name.
        var agentStart = Stopwatch.GetTimestamp();
        ResolvedAgentRuntime? resolved;
        using (NodeActivitySource.Source.StartActivity("chat.turn.resolve_agent"))
        {
            resolved = await _agentDefinitionResolver.ResolveAsync(effectiveAgentId, activeModel, retrievalQuery, supportsTools, honorModelProfile: !userPickedConcreteModel, activeModelIsCloud,
                                                        cancellationToken);
        }

        var agentMs = Stopwatch.GetElapsedTime(agentStart).TotalMilliseconds;

        // A tool-capable orchestrator definition resolves a compiled spec onto the package and the runner branches to
        // the handoff workflow; any null result leaves the package single-agent and byte-identical.
        var orchestrationStart = Stopwatch.GetTimestamp();
        OrchestrationResolution orchestration;
        using (NodeActivitySource.Source.StartActivity("chat.turn.resolve_orchestration"))
        {
            orchestration = await ResolveOrchestrationAsync(effectiveAgentId, resolved, activeModel, retrievalQuery, supportsTools, cancellationToken);
        }

        var orchestrationMs = Stopwatch.GetElapsedTime(orchestrationStart).TotalMilliseconds;

        // The single source of truth for the model that runs this turn: the honored pin, else the active model. Both
        // the runtime package AND the persisted attribution stamp from it, so the label cannot disagree with the run.
        var effectiveModel = resolved?.ModelProfile ?? activeModel;

        // The EFFECTIVE model's provider locality, which gates node-local private-data exposure. With no bound agent or
        // no pin the effective model IS the active model, so the active-model flag is reused.
        var effectiveModelIsCloud = resolved?.EffectiveModelIsCloud ?? activeModelIsCloud;

        // Read from the EFFECTIVE model, whose chat template owns this flag, or a pin llama-server cannot cap grades as
        // if it could. It is NOT on ResolvedAgentRuntime, which the FROZEN v1 benchmark snapshot embeds verbatim.
        var reasoningBudgetEnforceable = capabilities.ReasoningBudgetEnforceable;
        if (!string.Equals(effectiveModel, capabilityModel, StringComparison.Ordinal))
        {
            var effectiveCapabilities = await _modelCapabilityResolver.ResolveAsync(effectiveModel, cancellationToken);
            reasoningBudgetEnforceable = effectiveCapabilities.ReasoningBudgetEnforceable;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Chat-turn resolution completed in {ElapsedMs:F2} ms (capabilities={CapabilitiesMs:F2} ms, agent={AgentMs:F2} ms, orchestration={OrchestrationMs:F2} ms; boundAgent={HasBoundAgent}, orchestration={HasOrchestration}).",
                Stopwatch.GetElapsedTime(resolveStartTimestamp).TotalMilliseconds,
                capabilitiesMs,
                agentMs,
                orchestrationMs,
                effectiveAgentId is not null,
                orchestration.Orchestration is not null);
        }

        // The caller's flag is raised from the LOCAL-default head alone, before the pin is known, so it survives only
        // when neither default nor pin produced a model. See docs/wiki/05-chat.md, "Model provenance and the auto-swap permission".
        var allowAutoModelSwap = !userPickedConcreteModel && resolved?.ModelProfile is null;

        return new ChatTurnResolution
        {
            ActiveModel = activeModel,
            EffectiveModel = effectiveModel,
            Resolved = resolved,
            OrchestrationOutcome = orchestration,
            SupportsThinking = supportsThinking,
            SupportsTools = supportsTools,
            SupportsVision = supportsVision,
            RequiresInstalledChatModel = requiresInstalledChatModel && effectiveModel is null,
            ActiveModelIsCloud = activeModelIsCloud,
            EffectiveModelIsCloud = effectiveModelIsCloud,
            ReasoningBudgetEnforceable = reasoningBudgetEnforceable,
            AllowAutoModelSwap = allowAutoModelSwap
        };
    }

    /// <summary>
    ///     Resolves a compiled orchestration spec for a bound orchestrator definition, or a degraded resolution that
    ///     runs the turn single-agent.
    /// </summary>
    /// <remarks>
    ///     Only a bound conversation triggers the extra record fetch; anything else returns
    ///     <see cref="OrchestrationResolution.NotOrchestrated" /> without resolving, so the single-agent path stays
    ///     byte-identical and no degradation notice is raised for an agent that never asked for orchestration.
    /// </remarks>
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

        // The resolver already loaded and decrypted this definition, so its Kind is reused instead of a SECOND uncached
        // fetch: only a bound orchestrator pays the reload that gives the compiler the full definition.
        if (resolved is not { Kind: AgentDefinitionKind.Orchestrator })
        {
            return OrchestrationResolution.NotOrchestrated;
        }

        var definition = await _agentDefinitionStore.GetByIdAsync(definitionId, cancellationToken);
        if (definition is null || definition.Kind != AgentDefinitionKind.Orchestrator)
        {
            return OrchestrationResolution.NotOrchestrated;
        }

        // Orchestration resolves each participant's knowledge-tool locality from its own effective model internally, so
        // no turn-level cloud flag is threaded here.
        return await _orchestrationResolver.ResolveAsync(definition, activeModel, retrievalQuery, supportsTools, cancellationToken);
    }
}
