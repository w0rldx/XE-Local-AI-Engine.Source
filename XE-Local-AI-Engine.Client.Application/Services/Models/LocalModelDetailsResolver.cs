namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;

/// <inheritdoc />
internal sealed class LocalModelDetailsResolver : ILocalModelDetailsResolver
{
    /// <summary>The provider key a llama.cpp-served GGUF resolves to.</summary>
    /// <remarks>
    ///     Mirrors <c>LocalModelProviders.LlamaCpp</c>, which is an endpoint-layer wire constant this project cannot reference; the same
    ///     literal is already the default in the provider resolver's own registration.
    /// </remarks>
    private const string LlamaCppProviderName = "llamacpp";

    private readonly ICloudModelResolver _cloudModelResolver;
    private readonly IGgufModelStore _ggufModelStore;
    private readonly ILogger<LocalModelDetailsResolver> _logger;
    private readonly IModelTrustResolver _modelTrustResolver;
    private readonly IOllamaModelService _modelService;
    private readonly ILocalModelProviderResolver _providerResolver;

    public LocalModelDetailsResolver(IOllamaModelService modelService,
        ILocalModelProviderResolver providerResolver,
        IGgufModelStore ggufModelStore,
        ICloudModelResolver cloudModelResolver,
        IModelTrustResolver modelTrustResolver,
        ILogger<LocalModelDetailsResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(cloudModelResolver);
        ArgumentNullException.ThrowIfNull(ggufModelStore);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(modelTrustResolver);
        ArgumentNullException.ThrowIfNull(modelService);
        ArgumentNullException.ThrowIfNull(providerResolver);
        _cloudModelResolver = cloudModelResolver;
        _ggufModelStore = ggufModelStore;
        _logger = logger;
        _modelTrustResolver = modelTrustResolver;
        _modelService = modelService;
        _providerResolver = providerResolver;
    }

    public async Task<LocalModelDetailsResolution> ResolveAsync(string modelName, CancellationToken cancellationToken = default)
    {
        // A Codex cloud model id (e.g. gpt-5.5) is NOT a local Ollama model: probing the local runtime's /api/show for it 500s. Model details
        // (context window, template, license) are a local-runtime concept, so a cloud id has no local details — a clean 404, not a 500.
        if (CodexModelCatalog.IsCodexModel(modelName))
        {
            return new LocalModelDetailsResolution.NoLocalDetails();
        }

        // An external OpenAI-compatible model has no local runtime to probe either, but unlike the cloud ids above it has ONE detail the chat
        // context meter needs: the operator-declared context window. Template, system prompt and license stay null; a gone registration 404s.
        if (ExternalModelId.HasExternalScheme(modelName))
        {
            var registration = await _modelTrustResolver.TryResolveExternalAsync(modelName, cancellationToken);
            return registration is null
                ? new LocalModelDetailsResolution.NoLocalDetails()
                : new LocalModelDetailsResolution.External(registration);
        }

        // An Azure Foundry deployment id is likewise NOT a local Ollama model: context window, template and license are local-runtime
        // concepts an Azure deployment has no equivalent of, and probing /api/show would 500. No local details, matching the Codex branch.
        if (await _cloudModelResolver.IsAzureFoundryDeploymentAsync(modelName, cancellationToken))
        {
            return new LocalModelDetailsResolution.NoLocalDetails();
        }

        // Route by the model's provider BEFORE probing: a GGUF (llama.cpp) model has no Ollama /api/show entry, so its details come from the
        // GGUF store and a GGUF selection never touches Ollama, avoiding the desktop-mode connect stall and a 404. Provider resolution runs on the DECODED name, so resolved and probed names match.
        var providerName = await _providerResolver.ResolveProviderNameForModelAsync(modelName, cancellationToken);
        if (string.Equals(providerName, LlamaCppProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return await ResolveGgufAsync(modelName, cancellationToken);
        }

        try
        {
            return new LocalModelDetailsResolution.Ollama(await _modelService.ShowModelDetailsAsync(modelName, cancellationToken));
        }
        catch (HttpRequestException exception)
        {
            // Details come from the Ollama daemon's /api/show, and in desktop mode no endpoint is running, so the probe throws a connection
            // error: an absence of local details, not a server fault — a clean 404, logged at Debug because the chat UI polls this per model.
            _logger.LogDebug(exception, "Model details unavailable for '{ModelName}': the local Ollama runtime is unreachable.", modelName);
            return new LocalModelDetailsResolution.NoLocalDetails();
        }
    }

    // Details for a llama.cpp-served GGUF come from the installed-model registry — no Ollama probe. A model that resolves to llamacpp but is
    // not in that registry (a stale map row, or one removed on disk) has no details, matching Ollama's "no entry".
    private async Task<LocalModelDetailsResolution> ResolveGgufAsync(string modelName, CancellationToken cancellationToken)
    {
        var installed = await _ggufModelStore.ListInstalledModelsAsync(cancellationToken);
        var descriptor = installed.FirstOrDefault(model => string.Equals(model.ModelName, modelName, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            return new LocalModelDetailsResolution.NoLocalDetails();
        }

        var effectiveContextTokens = await TryResolveEffectiveContextAsync(modelName, cancellationToken);
        return new LocalModelDetailsResolution.Gguf(descriptor, effectiveContextTokens);
    }

    // Best-effort: the effective context window the running llama.cpp chat process loaded (null when none is warm or the
    // runtime does not report it). Never fails the details response — the meter simply falls back to MaxContextTokens.
    private async Task<int?> TryResolveEffectiveContextAsync(string modelName, CancellationToken cancellationToken)
    {
        try
        {
            var provider = _providerResolver.ResolveProvider(LlamaCppProviderName);
            var runtimeInfo = await provider.GetRuntimeInfoAsync(modelName, cancellationToken);
            return runtimeInfo is { EffectiveContextTokens: > 0 } info ? info.EffectiveContextTokens : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Effective context window could not be resolved for '{ModelName}'.", modelName);
            return null;
        }
    }
}
