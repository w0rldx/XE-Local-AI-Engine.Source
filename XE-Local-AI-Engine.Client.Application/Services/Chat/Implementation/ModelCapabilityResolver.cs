namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;

/// <summary>
///     Default <see cref="IModelCapabilityResolver" />, routing the capability lookup by the model's provider so an
///     id is never classified against a runtime that has never seen it.
/// </summary>
/// <remarks>
///     Codex and Azure Foundry ids use their declared matrices, an external OpenAI-compatible model uses the
///     operator's own declarations, a llama.cpp GGUF reads its offline chat-template capabilities with no Ollama
///     probe and no network, and only an Ollama-routed model hits <c>/api/show</c>, cache-first. This is the one
///     provider-routing decision in the codebase: both the orchestration resolver, per participant, and
///     <see cref="ChatTurnResolver" />, per turn, resolve through it.
/// </remarks>
public sealed class ModelCapabilityResolver : IModelCapabilityResolver
{
    // What an ext: id resolves to when its registration is gone: not capable, and cloud, so the private-data gates
    // withhold. Reached for a deleted connection, a corrupt store, or a hand-edited id in a saved agent.
    private static readonly ModelCapabilitySnapshot UnresolvedExternal = new(SupportsThinking: false, SupportsTools: false, IsCloud: true)
    {
        ReasoningBudgetEnforceable = true
    };

    // The safe default: not thinking-capable, not tool-capable, and node-local.
    private static readonly ModelCapabilitySnapshot NotCapableLocal = new(SupportsThinking: false, SupportsTools: false, IsCloud: false);
    private readonly IModelClassificationService _modelClassificationService;
    private readonly ILocalModelProviderResolver _localModelProviderResolver;
    private readonly IGgufModelCapabilityResolver _ggufModelCapabilityResolver;
    private readonly IActiveCloudChatClientFactory _activeCloudChatClientFactory;
    private readonly IModelTrustResolver _modelTrustResolver;
    private readonly ILogger<ModelCapabilityResolver> _logger;

    public ModelCapabilityResolver(
        IModelClassificationService modelClassificationService,
        ILocalModelProviderResolver localModelProviderResolver,
        IGgufModelCapabilityResolver ggufModelCapabilityResolver,
        IActiveCloudChatClientFactory activeCloudChatClientFactory,
        IModelTrustResolver modelTrustResolver,
        ILogger<ModelCapabilityResolver> logger)
    {
        _modelClassificationService = modelClassificationService;
        _localModelProviderResolver = localModelProviderResolver;
        _ggufModelCapabilityResolver = ggufModelCapabilityResolver;
        _activeCloudChatClientFactory = activeCloudChatClientFactory;
        _modelTrustResolver = modelTrustResolver;
        _logger = logger;
    }

    public async Task<ModelCapabilitySnapshot> ResolveAsync(string? model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return NotCapableLocal;
        }

        // A Codex cloud model is NOT an Ollama model, so its declared matrix replaces an /api/show probe the runtime
        // could only mis-detect. Thinking is on and tools track the V0 matrix; a cloud provider ignores `think`.
        if (CodexModelCatalog.IsCodexModel(model))
        {
            return new ModelCapabilitySnapshot(SupportsThinking: true, CodexProviderCapabilities.V0.SupportsToolCalling, IsCloud: true)
            {
                // The reasoning-budget marker is consumed only by the llama.cpp chat client, so it never reaches the
                // Codex wire; report the budget enforceable so the cloud path stays byte-identical.
                ReasoningBudgetEnforceable = true
            };
        }

        // An external OpenAI-compatible model is classified from the OPERATOR'S declarations, never a probe. It is
        // checked before cloud routing, which an ext: id falls through and would report a hosted endpoint as local.
        if (ExternalModelId.HasExternalScheme(model))
        {
            if (await _modelTrustResolver.TryResolveExternalAsync(model, cancellationToken) is not { } registration)
            {
                return UnresolvedExternal;
            }

            return new ModelCapabilitySnapshot(registration.Model.SupportsReasoning,
                registration.Model.SupportsTools,
                registration.Connection.Locality == ExternalProviderLocality.Cloud)
            {
                SupportsVision = registration.Model.SupportsVision,
                // Vacuously enforceable: this provider emits no llama-server reasoning-budget field, so no cap can be
                // silently accepted and ignored, and reporting false would suppress a marker nothing here reads.
                ReasoningBudgetEnforceable = true
            };
        }

        // Cloud LOCALITY comes from the SAME routing snapshot the factory routes from, never an independent credential
        // read that could fail and classify an egressing participant as local; a read failure FAILS CLOSED to cloud.
        var (routesToCloud, routingFaulted) = CloudRoutingClassifier.Classify(_activeCloudChatClientFactory, _logger, model);
        if (routesToCloud)
        {
            return routingFaulted
                ? new ModelCapabilitySnapshot(SupportsThinking: false, SupportsTools: false, IsCloud: true)
                : new ModelCapabilitySnapshot(SupportsThinking: false, AzureFoundryProviderCapabilities.V0.SupportsToolCalling, IsCloud: true);
        }

        // /api/show only makes sense for an Ollama-routed model: a GGUF has no entry and desktop mode has no daemon, so
        // the probe would stall every send. A GGUF reads its chat template offline; other providers take the default.
        var providerName = await _localModelProviderResolver
                                 .ResolveProviderNameForModelAsync(model, cancellationToken);
        if (!string.Equals(providerName, OllamaLocalModelProvider.OllamaProviderName, StringComparison.OrdinalIgnoreCase))
        {
            var ggufCapabilities = await _ggufModelCapabilityResolver
                                         .TryResolveAsync(model, cancellationToken);
            // A llama.cpp (GGUF) or other non-Ollama-but-node-local model is LOCAL. Vision rides the GGUF descriptor's
            // projector-gated flag — the only path that can advertise it (cloud/Ollama stay non-vision here).
            return ggufCapabilities is { } caps
                ? new ModelCapabilitySnapshot(caps.SupportsThinking, caps.SupportsTools, IsCloud: false)
                {
                    SupportsVision = caps.SupportsVision,
                    // The ONE path that can report a budget as unenforceable: llama.cpp is the only runtime that reads
                    // the budget, and the GGUF descriptor is the only place its chat template was classified.
                    ReasoningBudgetEnforceable = caps.ReasoningBudgetEnforceable
                }
                : NotCapableLocal;
        }

        var classifications = await _modelClassificationService
                                    .ClassifyAsync([new ModelIdentity(model, Digest: null)], cancellationToken);
        if (!classifications.TryGetValue(model, out var classification))
        {
            return NotCapableLocal;
        }

        // An Ollama-routed model runs on the node — local. Vision is not resolved on the Ollama path (llama.cpp is the
        // default runtime and the mmproj-gated GGUF path owns vision); it stays the safe non-vision default here.
        return new ModelCapabilitySnapshot(ModelKindDetector.SupportsThinking(classification.Capabilities),
            ModelKindDetector.SupportsTools(classification.Capabilities),
            IsCloud: false)
        {
            // Ollama ignores the in-process budget marker (its mapper reads a fixed option allowlist), so there is
            // nothing to suppress on this wire — report enforceable and keep the Ollama path byte-identical.
            ReasoningBudgetEnforceable = true
        };
    }
}
