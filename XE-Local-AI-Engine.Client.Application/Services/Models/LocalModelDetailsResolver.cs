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
    /// <summary>
    ///     The provider key a llama.cpp-served GGUF resolves to. Mirrors <c>LocalModelProviders.LlamaCpp</c>, which is
    ///     an endpoint-layer wire constant this project cannot reference; the same literal is already the default in
    ///     the provider resolver's own registration.
    /// </summary>
    private const string LlamaCppProviderName = "llamacpp";

    private readonly ICloudModelResolver _cloudModelResolver;
    private readonly IGgufModelStore _ggufModelStore;
    private readonly ILogger<LocalModelDetailsResolver> _logger;
    private readonly IModelTrustResolver _modelTrustResolver;
    private readonly IOllamaModelService _modelService;
    private readonly ILocalModelProviderResolver _providerResolver;

    public LocalModelDetailsResolver(
        IOllamaModelService modelService,
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
        // A Codex cloud model id (e.g. gpt-5.5) is NOT a local Ollama model: probing the local runtime's /api/show
        // for it 500s (Ollama has no such model). Model details (context window, template, license) are a
        // local-runtime concept, so a cloud id has no local details — a clean 404 instead of a 500. The chat
        // UI should not request local details for a cloud model at all.
        if (CodexModelCatalog.IsCodexModel(modelName))
        {
            return new LocalModelDetailsResolution.NoLocalDetails();
        }

        // An external OpenAI-compatible model has no local runtime to probe either, but unlike the cloud ids above it
        // does have ONE detail the chat context meter needs: the context window its operator declared. Everything else
        // (template, system prompt, license) is an Ollama Modelfile concept the remote endpoint has no equivalent of,
        // so those stay null. An id whose registration is gone is a clean 404, exactly like a stale GGUF map row.
        if (ExternalModelId.HasExternalScheme(modelName))
        {
            var registration = await _modelTrustResolver.TryResolveExternalAsync(modelName, cancellationToken);
            return registration is null
                ? new LocalModelDetailsResolution.NoLocalDetails()
                : new LocalModelDetailsResolution.External(registration);
        }

        // An Azure Foundry deployment id is likewise NOT a local Ollama model: model details (context window, template,
        // license) are local-runtime concepts an Azure deployment has no equivalent of, and probing /api/show for it
        // would 500. No local details, matching the Codex branch above.
        if (await _cloudModelResolver.IsAzureFoundryDeploymentAsync(modelName, cancellationToken))
        {
            return new LocalModelDetailsResolution.NoLocalDetails();
        }

        // Route by the model's provider BEFORE probing: a GGUF (llama.cpp) model has no Ollama /api/show entry, so its
        // details come from the GGUF store, not the Ollama daemon. This also means a GGUF selection never touches Ollama
        // — in desktop mode (no Ollama daemon) that avoids both the connect stall and the 404 the old Ollama-only path
        // returned. Provider resolution runs on the DECODED name so "validated/resolved name == probed name".
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
            // Details come from the Ollama daemon's /api/show. In desktop mode the Ollama endpoint isn't running at all,
            // so the probe throws a connection error. That is an absence of local details, not a server fault: degrade
            // to a clean 404 instead of bubbling a 500. Debug, not Warning: the chat UI polls this per selected model —
            // logging a Warning + stack trace here would flood the console. The 404 is the intended graceful degradation.
            _logger.LogDebug(exception, "Model details unavailable for '{ModelName}': the local Ollama runtime is unreachable.", modelName);
            return new LocalModelDetailsResolution.NoLocalDetails();
        }
    }

    // Details for a llama.cpp-served GGUF come from the installed-model registry — no Ollama probe. A model that
    // resolves to llamacpp but isn't in the installed registry (a stale map row, or one removed on disk) has no
    // details, matching Ollama's "no entry".
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
