namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

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
    ICloudCredentialStore cloudCredentialStore,
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
        // default) so a plain chat still works without tripping the 400. Cache hit issues no /api/show call.
        var (supportsThinking, supportsTools) = await ResolveModelCapabilitiesAsync(activeModel, cancellationToken).ConfigureAwait(false);

        // Resolve the effective agent definition. A null result — no effective id (the seed is missing), or an id whose
        // definition was deleted — keeps the default persona: the embedded system prompt, the full capability-gated
        // offer, and agent version 1. When resolved, the definition supplies the system prompt, the tool offer (full for
        // the Default Assistant, intersected otherwise), the pinned model profile, the reasoning effort, the version
        // that feeds the config hash, AND the attribution snapshot (id + display name). The retrieval query is the
        // relevance-retrieval query (inert below the threshold / unbound, so the prompt stays byte-identical).
        var resolved = await agentDefinitionResolver.ResolveAsync(effectiveAgentId, activeModel, retrievalQuery, supportsTools, honorModelProfile: !userPickedConcreteModel, cancellationToken)
                                                    .ConfigureAwait(false);

        // When the effective definition is a tool-capable orchestrator, resolve a compiled orchestration spec to carry
        // on the package — the runner branches to the handoff workflow. A null result (not an orchestrator, an
        // empty/invalid topology, an incapable model, or too few capable participants) leaves the package single-agent,
        // keeping the unbound/single-agent path byte-identical. The same retrieval query drives per-participant retrieval.
        var orchestration = await ResolveOrchestrationAsync(effectiveAgentId, activeModel, retrievalQuery, supportsTools, cancellationToken).ConfigureAwait(false);

        // The single source of truth for the model that actually runs this turn: the resolved pin when honored (the
        // resolver returned null for ModelProfile when the user's explicit pick suppressed it), otherwise the active
        // model. Both the runtime package AND the persisted assistant-message attribution are stamped from this so the
        // label can never disagree with what ran.
        var effectiveModel = resolved?.ModelProfile ?? activeModel;

        return new ChatTurnResolution(activeModel, effectiveModel, resolved, orchestration, supportsThinking, supportsTools, requiresInstalledChatModel);
    }

    /// <summary>
    ///     Resolves the active model's advertised <c>thinking</c>/<c>tools</c> capabilities via the shared classification
    ///     service (cache-first; no <c>/api/show</c> call on a cache hit). A null/blank model or any detection miss
    ///     resolves to NOT-capable for both — the safe default that omits the think field (avoiding the Ollama 400) and
    ///     withholds the tool offer while still allowing a plain chat.
    /// </summary>
    private async Task<(bool SupportsThinking, bool SupportsTools)> ResolveModelCapabilitiesAsync(string? activeModel, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(activeModel))
        {
            return (false, false);
        }

        // A Codex cloud model is NOT an Ollama model: classifying it against the local runtime's /api/show would
        // mis-detect it (the runtime has never seen it). Use the Codex provider's declared capability matrix
        // instead. Codex models reason by default, so thinking is on; tool calling tracks the V0 matrix, which now
        // ENABLES tools for all Codex ids (de-risk verified — encrypted reasoning round-trips through the stateless
        // tool loop). Cloud providers ignore the unknown think property, so the reasoning gate stays inert on the wire.
        if (CodexModelCatalog.IsCodexModel(activeModel))
        {
            return (SupportsThinking: true, CodexProviderCapabilities.V0.SupportsToolCalling);
        }

        // An Azure Foundry deployment id is NOT an Ollama model either: probing /api/show for it would 500/stall.
        // When the active model matches a stored Azure deployment name, advertise the Azure provider's declared
        // capability matrix instead of an Ollama classification, so an Azure id never falls through to the local probe.
        if (await IsAzureFoundryModelAsync(activeModel, cancellationToken).ConfigureAwait(false))
        {
            return (SupportsThinking: false, SupportsTools: AzureFoundryProviderCapabilities.V0.SupportsToolCalling);
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
            return ggufCapabilities is { } caps
                ? (caps.SupportsThinking, caps.SupportsTools)
                : (SupportsThinking: false, SupportsTools: false);
        }

        var classifications = await modelClassificationService
                                    .ClassifyAsync([(activeModel, null)], cancellationToken)
                                    .ConfigureAwait(false);
        if (!classifications.TryGetValue(activeModel, out var classification))
        {
            return (false, false);
        }

        return (ModelKindDetector.SupportsThinking(classification.Capabilities),
            ModelKindDetector.SupportsTools(classification.Capabilities));
    }

    /// <summary>
    ///     True when <paramref name="activeModel" /> matches one of the stored Azure Foundry connection's deployment
    ///     names (ordinal, case-insensitive). A best-effort read: any failure resolving the encrypted config is treated
    ///     as "not Azure" so the capability gate falls through to its existing (safe) classification rather than failing
    ///     the send.
    /// </summary>
    private async Task<bool> IsAzureFoundryModelAsync(string activeModel, CancellationToken cancellationToken)
    {
        try
        {
            var config = await cloudCredentialStore.LoadConfigAsync(cancellationToken).ConfigureAwait(false);
            var connection = config?.AzureFoundry;
            return connection is { Models.Count: > 0 }
                   && connection.Models.Any(model => string.Equals(model.DeploymentName, activeModel, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Azure Foundry deployment match could not be resolved for '{Model}'.", activeModel);
            return false;
        }
    }

    /// <summary>
    ///     Resolves a compiled orchestration spec for a bound orchestrator definition (orchestration), or <c>null</c> to
    ///     run the turn single-agent. Only a bound conversation triggers the extra record fetch; an unbound conversation
    ///     or a non-orchestrator definition returns <c>null</c> without resolving, so the single-agent path is byte-identical.
    /// </summary>
    private async Task<ResolvedOrchestration?> ResolveOrchestrationAsync(Guid? agentDefinitionId,
        string? activeModel,
        string? retrievalQuery,
        bool supportsTools,
        CancellationToken cancellationToken)
    {
        if (agentDefinitionId is not { } definitionId)
        {
            return null;
        }

        var definition = await agentDefinitionStore.GetByIdAsync(definitionId, cancellationToken).ConfigureAwait(false);
        if (definition is null || definition.Kind != AgentDefinitionKind.Orchestrator)
        {
            return null;
        }

        return await orchestrationResolver.ResolveAsync(definition, activeModel, retrievalQuery, supportsTools, cancellationToken).ConfigureAwait(false);
    }
}
