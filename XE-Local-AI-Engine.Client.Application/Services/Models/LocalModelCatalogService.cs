namespace XE_Local_AI_Engine.Client.Services.Models;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.CodexOAuth.Contracts;
using XE_Local_AI_Engine.Providers.CodexOAuth.Options;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;

/// <summary>Represents local model catalog service.</summary>
/// <remarks>
///     Each source is read independently and degrades on its own, so a picker built on a node with no Ollama, no cloud session and an
///     unreadable GGUF registry still answers with whatever the remaining sources have.
/// </remarks>
public sealed class LocalModelCatalogService : ILocalModelCatalogService
{
    private static readonly IReadOnlyDictionary<string, ModelClassificationResult> NoClassifications =
        new Dictionary<string, ModelClassificationResult>(StringComparer.OrdinalIgnoreCase);

    private readonly IModelClassificationService _classificationService;
    private readonly ICloudModelResolver _cloudModelResolver;
    private readonly CodexOptions _codexOptions;
    private readonly ICodexTokenStore _codexTokenStore;
    private readonly IExternalProviderRegistry _externalProviderRegistry;
    private readonly IGgufModelStore _ggufModelStore;
    private readonly IOptions<LocalChatAgentOptions> _localChatOptions;
    private readonly ILogger<LocalModelCatalogService> _logger;
    private readonly IOllamaModelService _modelService;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly TimeProvider _timeProvider;

    public LocalModelCatalogService(
        IOllamaModelService modelService,
        IModelClassificationService classificationService,
        IGgufModelStore ggufModelStore,
        INodeRuntimeSettings runtimeSettings,
        IOptions<LocalChatAgentOptions> localChatOptions,
        ICodexTokenStore codexTokenStore,
        IOptions<CodexOptions> codexOptions,
        ICloudModelResolver cloudModelResolver,
        IExternalProviderRegistry externalProviderRegistry,
        TimeProvider timeProvider,
        ILogger<LocalModelCatalogService> logger)
    {
        ArgumentNullException.ThrowIfNull(classificationService);
        _classificationService = classificationService;
        ArgumentNullException.ThrowIfNull(cloudModelResolver);
        _cloudModelResolver = cloudModelResolver;
        _codexOptions = (codexOptions ?? throw new ArgumentNullException(nameof(codexOptions))).Value;
        ArgumentNullException.ThrowIfNull(codexTokenStore);
        _codexTokenStore = codexTokenStore;
        ArgumentNullException.ThrowIfNull(externalProviderRegistry);
        _externalProviderRegistry = externalProviderRegistry;
        ArgumentNullException.ThrowIfNull(ggufModelStore);
        _ggufModelStore = ggufModelStore;
        ArgumentNullException.ThrowIfNull(localChatOptions);
        _localChatOptions = localChatOptions;
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        ArgumentNullException.ThrowIfNull(modelService);
        _modelService = modelService;
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        _runtimeSettings = runtimeSettings;
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public async Task<LocalModelCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        // The effective selected model resolves through the accessor, with the stored DefaultModelName winning over the appsettings
        // Agent:LocalChat:DefaultModel seed. The configured default stays that seed, so the picker can surface "node default" separately.
        var selectedModelName = await _runtimeSettings.GetDefaultModelNameAsync(cancellationToken);

        // Cloud models (Codex + Azure Foundry) and installed GGUFs are served independently of Ollama, so they are
        // resolved up front and survive an unreachable Ollama below.
        var hasCodexSession = await HasUsableCodexSessionAsync(cancellationToken);
        var azureConnection = await _cloudModelResolver.ResolveAzureFoundryConnectionAsync(cancellationToken);
        var ggufModels = await ResolveInstalledGgufModelsAsync(cancellationToken);
        var externalModels = await ResolveExternalModelsAsync(cancellationToken);

        var (ollamaModels, classifications) = await ResolveOllamaModelsAsync(cancellationToken);

        return new LocalModelCatalog
        {
            SelectedModelName = selectedModelName,
            ConfiguredDefaultModelName = _localChatOptions.Value.DefaultModel,
            OllamaModels = ollamaModels,
            Classifications = classifications,
            InstalledGgufModels = ggufModels,
            HasUsableCodexSession = hasCodexSession,
            AzureFoundryConnection = azureConnection,
            ExternalModels = externalModels
        };
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RunningModelSnapshot>> ListRunningOllamaModelsAsync(CancellationToken cancellationToken = default)
    {
        return _modelService.ListRunningModelsAsync(cancellationToken);
    }

    /// <summary>Enumerates the models registered on the operator's external OpenAI-compatible connections.</summary>
    /// <remarks>
    ///     A best-effort read like every other source: an unreadable encrypted store yields no external entries rather than failing the
    ///     whole catalog, so a corrupt external file can never take the local model picker down with it.
    /// </remarks>
    private async Task<IReadOnlyList<ExternalProviderModelRegistration>> ResolveExternalModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _externalProviderRegistry.ListRegistrationsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "External OpenAI-compatible model list could not be resolved.");
            return [];
        }
    }

    /// <summary>
    ///     Lists the Ollama runtime's models and their effective kinds, or <see langword="null" /> models when that runtime could not be
    ///     reached.
    /// </summary>
    /// <remarks>
    ///     An unreachable endpoint (<see cref="HttpRequestException" />) is expected in desktop mode and logs at Debug so it does not flood
    ///     the console; any OTHER failure is unexpected and stays at Warning. Classification is lazy and cached by content digest, so a
    ///     cache hit issues no <c>/api/show</c> call and repeated catalog reads are cheap.
    /// </remarks>
    private async Task<OllamaModelListing> ResolveOllamaModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var models = (await _modelService.ListLocalModelsAsync(cancellationToken)).ToArray();
            var classifications = await _classificationService
                                        .ClassifyAsync(models.Select(static model => new ModelIdentity(model.Name, model.Digest)), cancellationToken);

            return new OllamaModelListing(models, classifications);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (exception is HttpRequestException)
            {
                _logger.LogDebug(exception, "Ollama not reachable while loading the model list; returning installed GGUF/cloud models only.");
            }
            else
            {
                _logger.LogWarning(exception, "Local model list could not be loaded.");
            }

            return new OllamaModelListing(Models: null, NoClassifications);
        }
    }

    /// <summary>
    ///     Enumerates the installed GGUF models, served by the bundled llama.cpp runtime and independent of Ollama.
    /// </summary>
    /// <remarks>
    ///     A best-effort read: any failure (e.g. an unreadable registry) yields an empty list rather than failing the
    ///     whole catalog, so the Ollama path's availability is unaffected.
    /// </remarks>
    private async Task<IReadOnlyList<LocalModelDescriptor>> ResolveInstalledGgufModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _ggufModelStore.ListInstalledModelsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Installed GGUF model list could not be resolved.");
            return [];
        }
    }

    /// <summary>
    ///     True when a stored Codex session exists whose access token is non-expired (skew-adjusted — the same gate <c>cloud/codex/status</c>
    ///     uses).
    /// </summary>
    /// <remarks>
    ///     A best-effort read: any failure resolving the session offers no Codex models rather than failing the whole catalog.
    /// </remarks>
    private async Task<bool> HasUsableCodexSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var session = await _codexTokenStore.LoadAsync(cancellationToken);
            return session is not null && !session.IsExpired(_codexOptions.ExpirySkew, _timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Codex cloud model list could not be resolved.");
            return false;
        }
    }

    // The Ollama runtime's models and their effective kinds. Models is null — not empty — when the runtime could not
    // be reached, which the catalog renders differently from "reachable, but nothing installed".
    private sealed record OllamaModelListing(IReadOnlyList<OllamaModelSummary>? Models, IReadOnlyDictionary<string, ModelClassificationResult> Classifications);
}
