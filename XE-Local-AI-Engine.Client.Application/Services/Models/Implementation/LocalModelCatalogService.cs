namespace XE_Local_AI_Engine.Client.Services.Models.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.CodexOAuth.Contracts;
using XE_Local_AI_Engine.Providers.CodexOAuth.Options;

/// <summary>Represents local model catalog service.</summary>
/// <remarks>
///     Each source is read independently and degrades on its own, so a picker built on a node with no cloud session and an
///     unreadable GGUF registry still answers with whatever the remaining sources have.
/// </remarks>
public sealed class LocalModelCatalogService : ILocalModelCatalogService
{
    private readonly IModelClassificationStore _classificationStore;
    private readonly ICloudModelResolver _cloudModelResolver;
    private readonly CodexOptions _codexOptions;
    private readonly ICodexTokenStore _codexTokenStore;
    private readonly IExternalProviderRegistry _externalProviderRegistry;
    private readonly IGgufModelStore _ggufModelStore;
    private readonly IOptions<LocalChatAgentOptions> _localChatOptions;
    private readonly ILogger<LocalModelCatalogService> _logger;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly TimeProvider _timeProvider;

    public LocalModelCatalogService(IModelClassificationStore classificationStore,
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
        ArgumentNullException.ThrowIfNull(classificationStore);
        _classificationStore = classificationStore;
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

        var hasCodexSession = await HasUsableCodexSessionAsync(cancellationToken);
        var azureConnection = await _cloudModelResolver.ResolveAzureFoundryConnectionAsync(cancellationToken);
        var ggufModels = await ResolveInstalledGgufModelsAsync(cancellationToken);
        var externalModels = await ResolveExternalModelsAsync(cancellationToken);
        var classifications = await ResolveClassificationsAsync(cancellationToken);

        return new LocalModelCatalog
        {
            SelectedModelName = selectedModelName,
            ConfiguredDefaultModelName = _localChatOptions.Value.DefaultModel,
            Classifications = classifications,
            InstalledGgufModels = ggufModels,
            HasUsableCodexSession = hasCodexSession,
            AzureFoundryConnection = azureConnection,
            ExternalModels = externalModels
        };
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

    /// <summary>Reads the stored classification rows; a failed read loses the overrides for this list, never the list itself.</summary>
    private async Task<IReadOnlyDictionary<string, ModelClassificationRecord>> ResolveClassificationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var records = await _classificationStore.ListAsync(cancellationToken);
            return records.ToDictionary(static record => record.ModelName, StringComparer.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Model classifications could not be read.");
            return new Dictionary<string, ModelClassificationRecord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Enumerates the installed GGUF models, served by the bundled llama.cpp runtime.</summary>
    /// <remarks>
    ///     A best-effort read: any failure (e.g. an unreadable registry) yields an empty list rather than failing the
    ///     whole catalog.
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
}
