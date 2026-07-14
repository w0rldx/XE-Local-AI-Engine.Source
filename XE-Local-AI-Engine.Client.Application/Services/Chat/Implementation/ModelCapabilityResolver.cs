namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;

/// <summary>
///     Default <see cref="IModelCapabilityResolver" />. Routes the capability lookup by the model's provider so an id is
///     never classified against a runtime that has never seen it: Codex and Azure Foundry ids use their declared
///     capability matrices, a llama.cpp GGUF reads its offline chat-template capabilities (no Ollama probe, no network),
///     and only an Ollama-routed model hits <c>/api/show</c> (cache-first). Used by the orchestration resolver to
///     resolve each participant's thinking capability from its OWN effective model.
///     <para>
///         This is the SAME provider-routing decision that <see cref="ChatTurnResolver" />'s private
///         <c>ResolveModelCapabilitiesAsync</c> makes for the active model. The two are intentionally kept in step —
///         a change to the routing here MUST be mirrored there, and vice versa.
///     </para>
/// </summary>
public sealed class ModelCapabilityResolver(
    IModelClassificationService modelClassificationService,
    ILocalModelProviderResolver localModelProviderResolver,
    IGgufModelCapabilityResolver ggufModelCapabilityResolver,
    ICloudCredentialStore cloudCredentialStore,
    ILogger<ModelCapabilityResolver> logger) : IModelCapabilityResolver
{
    public async Task<(bool SupportsThinking, bool SupportsTools, bool IsCloud)> ResolveAsync(string? model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return (false, false, IsCloud: false);
        }

        // A Codex cloud model is NOT an Ollama model: classifying it against the local runtime's /api/show would
        // mis-detect it (the runtime has never seen it). Use the Codex provider's declared capability matrix
        // instead. Codex models reason by default, so thinking is on; tool calling tracks the V0 matrix, which now
        // ENABLES tools for all Codex ids (de-risk verified — encrypted reasoning round-trips through the stateless
        // tool loop). Cloud providers ignore the unknown think property, so the reasoning gate stays inert on the wire.
        if (CodexModelCatalog.IsCodexModel(model))
        {
            return (SupportsThinking: true, CodexProviderCapabilities.V0.SupportsToolCalling, IsCloud: true);
        }

        // An Azure Foundry deployment id is NOT an Ollama model either: probing /api/show for it would 500/stall.
        // When the active model matches a stored Azure deployment name, advertise the Azure provider's declared
        // capability matrix instead of an Ollama classification, so an Azure id never falls through to the local probe.
        if (await IsAzureFoundryModelAsync(model, cancellationToken).ConfigureAwait(false))
        {
            return (SupportsThinking: false, SupportsTools: AzureFoundryProviderCapabilities.V0.SupportsToolCalling, IsCloud: true);
        }

        // /api/show classification only makes sense for an Ollama-routed model. A llama.cpp (GGUF) model has no Ollama
        // entry, so probing the local runtime would always fail — and in desktop mode there is no Ollama daemon at all,
        // so the probe would stall (up to the connect timeout) on every send. Instead, read the GGUF's capabilities
        // detected offline from its chat template (cheap, cached per file — no Ollama probe, no network), matching the
        // model-list classification (LocalModelsMapper.ToLlamaCppModelResponses). Skip the doomed probe for any
        // non-Ollama provider; for a llama.cpp model the GGUF detection supplies thinking/tools, otherwise the safe
        // default applies.
        var providerName = await localModelProviderResolver
                                 .ResolveProviderNameForModelAsync(model, cancellationToken)
                                 .ConfigureAwait(false);
        if (!string.Equals(providerName, OllamaLocalModelProvider.OllamaProviderName, StringComparison.OrdinalIgnoreCase))
        {
            var ggufCapabilities = await ggufModelCapabilityResolver
                                         .TryResolveAsync(model, cancellationToken)
                                         .ConfigureAwait(false);
            // A llama.cpp (GGUF) or other non-Ollama-but-node-local model is LOCAL.
            return ggufCapabilities is { } caps
                ? (caps.SupportsThinking, caps.SupportsTools, IsCloud: false)
                : (SupportsThinking: false, SupportsTools: false, IsCloud: false);
        }

        var classifications = await modelClassificationService
                                    .ClassifyAsync([(model, null)], cancellationToken)
                                    .ConfigureAwait(false);
        if (!classifications.TryGetValue(model, out var classification))
        {
            return (false, false, IsCloud: false);
        }

        // An Ollama-routed model runs on the node — local.
        return (ModelKindDetector.SupportsThinking(classification.Capabilities),
            ModelKindDetector.SupportsTools(classification.Capabilities),
            IsCloud: false);
    }

    /// <summary>
    ///     True when <paramref name="model" /> matches one of the stored Azure Foundry connection's deployment names
    ///     (ordinal, case-insensitive). A best-effort read: any failure resolving the encrypted config is treated as
    ///     "not Azure" so the capability gate falls through to its existing (safe) classification rather than failing.
    /// </summary>
    private async Task<bool> IsAzureFoundryModelAsync(string model, CancellationToken cancellationToken)
    {
        try
        {
            var config = await cloudCredentialStore.LoadConfigAsync(cancellationToken).ConfigureAwait(false);
            var connection = config?.AzureFoundry;
            return connection is { Models.Count: > 0 }
                   && connection.Models.Any(deployment => string.Equals(deployment.DeploymentName, model, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Azure Foundry deployment match could not be resolved for '{Model}'.", model);
            return false;
        }
    }
}
