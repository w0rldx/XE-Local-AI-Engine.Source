namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;

/// <summary>
///     Shared per-turn resolution for the local send and regenerate paths. It resolves the active model's advertised
///     capabilities, the effective agent definition, and any compiled orchestration spec — the tail both
///     <see cref="NodeChatStreamService" /> and <see cref="NodeChatRegenerationService" /> perform identically after
///     each derives its own model/agent/retrieval-query head. Extracting it here keeps the capability gate (which side
///     probes Ollama vs reads GGUF chat-template capabilities) from drifting between the two paths.
/// </summary>
public sealed class ChatTurnResolver(
    IAgentDefinitionResolver agentDefinitionResolver,
    IAgentDefinitionStore agentDefinitionStore,
    IOrchestrationResolver orchestrationResolver,
    IModelClassificationService modelClassificationService,
    ILocalModelProviderResolver localModelProviderResolver,
    IGgufModelCapabilityResolver ggufModelCapabilityResolver,
    IActiveCloudChatClientFactory activeCloudChatClientFactory,
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
        // Resolve the active model's advertised Ollama capabilities ONCE so the think field and the tool offer are both
        // gated by what the model can actually do. A non-thinking model returns HTTP 400 for any think value; a
        // non-tools model cannot drive tool calls. Unknown/offline capabilities resolve to NOT-capable (the safe
        // default) so a plain chat still works without tripping the 400. Cache hit issues no /api/show call. The same
        // pass classifies provider LOCALITY (Codex / Azure Foundry = cloud) so the knowledge-tool provider-locality gate
        // reuses this per-turn resolution instead of adding its own hot-path lookup.
        // measure per-turn resolution cost so the before/after of the redundant-work removals (single agent-def
        // read, cached provider resolution) is observable. Debug-level + timestamp-based, so it costs a long on the hot
        // path when Debug logging is off. The same stages are wrapped in coarse spans so the audited silent
        // pre-spawn gap (a first send stalled here) shows up in exported traces, and the Debug log now breaks the total
        // down per stage. No high-cardinality attributes: the spans carry no model names, prompts, or ids.
        var resolveStartTimestamp = Stopwatch.GetTimestamp();
        using var resolveActivity = NodeActivitySource.Source.StartActivity("chat.turn.resolve");

        var capabilitiesStart = Stopwatch.GetTimestamp();
        (bool SupportsThinking, bool SupportsTools, bool SupportsVision, bool IsCloud) capabilities;
        using (NodeActivitySource.Source.StartActivity("chat.turn.resolve_capabilities"))
        {
            capabilities = await ResolveModelCapabilitiesAsync(activeModel, cancellationToken).ConfigureAwait(false);
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
    ///     Resolves the active model's advertised <c>thinking</c>/<c>tools</c> capabilities via the shared classification
    ///     service (cache-first; no <c>/api/show</c> call on a cache hit). A null/blank model or any detection miss
    ///     resolves to NOT-capable for both — the safe default that omits the think field (avoiding the Ollama 400) and
    ///     withholds the tool offer while still allowing a plain chat. The same provider-routing decision lives in
    ///     <see cref="ModelCapabilityResolver" /> for the per-participant orchestration path — change both together.
    /// </summary>
    private async Task<(bool SupportsThinking, bool SupportsTools, bool SupportsVision, bool IsCloud)> ResolveModelCapabilitiesAsync(string? activeModel, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(activeModel))
        {
            return (false, false, SupportsVision: false, IsCloud: false);
        }

        // A Codex cloud model is NOT an Ollama model: classifying it against the local runtime's /api/show would
        // mis-detect it (the runtime has never seen it). Use the Codex provider's declared capability matrix
        // instead. Codex models reason by default, so thinking is on; tool calling tracks the V0 matrix, which now
        // ENABLES tools for all Codex ids (de-risk verified — encrypted reasoning round-trips through the stateless
        // tool loop). Cloud providers ignore the unknown think property, so the reasoning gate stays inert on the wire.
        if (CodexModelCatalog.IsCodexModel(activeModel))
        {
            return (SupportsThinking: true, CodexProviderCapabilities.V0.SupportsToolCalling, SupportsVision: false, IsCloud: true);
        }

        // Azure/cloud LOCALITY is resolved from the SAME short-TTL routing snapshot the cloud factory routes from — the
        // single source of truth — never an independent credential-store read. An independent read could FAIL while the
        // factory still routes the deployment to Azure from its cached snapshot, classifying the request local while it
        // egresses to the cloud (the private-data gate would then leak). Sharing the snapshot removes that divergence,
        // and a genuine snapshot read failure FAILS CLOSED to cloud so the private-data gate withholds rather than leaks.
        // A non-Codex model that routes to a cloud provider is an Azure Foundry deployment (the only other cloud route),
        // so advertise Azure's capability matrix; on a fail-closed fault keep the conservative non-thinking/non-tools
        // default rather than asserting a matrix for a model that could not be classified. IsCloud feeds ONLY the
        // node-local private-data gates here (attachments + knowledge/file tools) — thinking/tools are the separate first
        // two tuple slots — so failing IsCloud closed does not disturb reasoning or tool-capability detection.
        var (routesToCloud, routingFaulted) = CloudRoutingClassifier.Classify(activeCloudChatClientFactory, logger, activeModel);
        if (routesToCloud)
        {
            return routingFaulted
                ? (SupportsThinking: false, SupportsTools: false, SupportsVision: false, IsCloud: true)
                : (SupportsThinking: false, SupportsTools: AzureFoundryProviderCapabilities.V0.SupportsToolCalling, SupportsVision: false, IsCloud: true);
        }

        // /api/show classification only makes sense for an Ollama-routed model. A llama.cpp (GGUF) model has no Ollama
        // entry, so probing the local runtime would always fail — and in desktop mode there is no Ollama daemon at all,
        // so the probe would stall (up to the connect timeout) on every send. Instead, read the GGUF's capabilities
        // detected offline from its chat template (cheap, cached per file — no Ollama probe, no network), matching the
        // model-list classification (LocalModelsMapper.ToLlamaCppModelResponses). Skip the doomed probe for any
        // non-Ollama provider; for a llama.cpp model the GGUF detection supplies thinking/tools, otherwise the safe
        // default applies.
        var providerName = await localModelProviderResolver
                                 .ResolveProviderNameForModelAsync(activeModel, cancellationToken)
                                 .ConfigureAwait(false);
        if (!string.Equals(providerName, OllamaLocalModelProvider.OllamaProviderName, StringComparison.OrdinalIgnoreCase))
        {
            var ggufCapabilities = await ggufModelCapabilityResolver
                                         .TryResolveAsync(activeModel, cancellationToken)
                                         .ConfigureAwait(false);
            // A llama.cpp (GGUF) or other non-Ollama-but-node-local model is LOCAL. Vision rides the GGUF descriptor's
            // projector-gated flag — the only path that can advertise it (cloud/Ollama stay non-vision here).
            return ggufCapabilities is { } caps
                ? (caps.SupportsThinking, caps.SupportsTools, caps.SupportsVision, IsCloud: false)
                : (SupportsThinking: false, SupportsTools: false, SupportsVision: false, IsCloud: false);
        }

        var classifications = await modelClassificationService
                                    .ClassifyAsync([new ModelIdentity(activeModel, Digest: null)], cancellationToken)
                                    .ConfigureAwait(false);
        if (!classifications.TryGetValue(activeModel, out var classification))
        {
            return (false, false, SupportsVision: false, IsCloud: false);
        }

        // An Ollama-routed model runs on the node — local. Vision is not resolved on the Ollama path (llama.cpp is the
        // default runtime and the mmproj-gated GGUF path owns vision); it stays the safe non-vision default here.
        return (ModelKindDetector.SupportsThinking(classification.Capabilities),
            ModelKindDetector.SupportsTools(classification.Capabilities),
            SupportsVision: false,
            IsCloud: false);
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
