namespace XE_Local_AI_Engine.Client.Services.Models;

using Microsoft.Extensions.Options;
using OllamaSharp.Models;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Options;

/// <summary>
///     Represents local model catalog service. Each source is read independently and degrades on its own, so a
///     picker built on a node with no Ollama, no cloud session and an unreadable GGUF registry still answers with
///     whatever the remaining sources have.
/// </summary>
public sealed class LocalModelCatalogService(
    IOllamaModelService modelService,
    IModelClassificationService classificationService,
    IGgufModelStore ggufModelStore,
    INodeRuntimeSettings runtimeSettings,
    IOptions<LocalChatAgentOptions> localChatOptions,
    ICodexTokenStore codexTokenStore,
    IOptions<CodexOptions> codexOptions,
    ICloudModelResolver cloudModelResolver,
    TimeProvider timeProvider,
    ILogger<LocalModelCatalogService> logger) : ILocalModelCatalogService
{
    private static readonly IReadOnlyDictionary<string, ModelClassificationResult> NoClassifications =
        new Dictionary<string, ModelClassificationResult>(StringComparer.OrdinalIgnoreCase);

    private readonly IModelClassificationService _classificationService = classificationService ?? throw new ArgumentNullException(nameof(classificationService));
    private readonly ICloudModelResolver _cloudModelResolver = cloudModelResolver ?? throw new ArgumentNullException(nameof(cloudModelResolver));
    private readonly CodexOptions _codexOptions = (codexOptions ?? throw new ArgumentNullException(nameof(codexOptions))).Value;
    private readonly ICodexTokenStore _codexTokenStore = codexTokenStore ?? throw new ArgumentNullException(nameof(codexTokenStore));
    private readonly IGgufModelStore _ggufModelStore = ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));
    private readonly IOptions<LocalChatAgentOptions> _localChatOptions = localChatOptions ?? throw new ArgumentNullException(nameof(localChatOptions));
    private readonly ILogger<LocalModelCatalogService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IOllamaModelService _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
    private readonly INodeRuntimeSettings _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<LocalModelCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        // The effective selected model resolves through the accessor (stored DefaultModelName > appsettings
        // Agent:LocalChat:DefaultModel seed); the configured default stays the appsettings seed so the picker can
        // still surface "node default" distinctly from the operator's selection.
        var selectedModelName = await _runtimeSettings.GetDefaultModelNameAsync(cancellationToken).ConfigureAwait(false);

        // Cloud models (Codex + Azure Foundry) and installed GGUFs are served independently of Ollama, so they are
        // resolved up front and survive an unreachable Ollama below.
        var hasCodexSession = await HasUsableCodexSessionAsync(cancellationToken).ConfigureAwait(false);
        var azureConnection = await _cloudModelResolver.ResolveAzureFoundryConnectionAsync(cancellationToken).ConfigureAwait(false);
        var ggufModels = await ResolveInstalledGgufModelsAsync(cancellationToken).ConfigureAwait(false);

        var (ollamaModels, classifications) = await ResolveOllamaModelsAsync(cancellationToken).ConfigureAwait(false);

        return new LocalModelCatalog(selectedModelName,
            _localChatOptions.Value.DefaultModel,
            ollamaModels,
            classifications,
            ggufModels,
            hasCodexSession,
            azureConnection);
    }

    /// <summary>
    ///     Lists the Ollama runtime's models and their effective kinds, or <see langword="null" /> models when that
    ///     runtime could not be reached. An unreachable endpoint (<see cref="HttpRequestException" />) is expected in
    ///     desktop mode and logs at Debug so it does not flood the console; any OTHER failure is unexpected and stays
    ///     at Warning. Classification is lazy and cached by content digest, so a cache hit issues no <c>/api/show</c>
    ///     call and repeated catalog reads are cheap.
    /// </summary>
    private async Task<(IReadOnlyList<Model>? Models, IReadOnlyDictionary<string, ModelClassificationResult> Classifications)>
        ResolveOllamaModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var models = (await _modelService.ListLocalModelsAsync(cancellationToken).ConfigureAwait(false)).ToArray();
            var classifications = await _classificationService
                                        .ClassifyAsync(models.Select(static model => new ModelIdentity(ReadModelName(model), model.Digest)), cancellationToken)
                                        .ConfigureAwait(false);

            return (models, classifications);
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

            return (null, NoClassifications);
        }
    }

    /// <summary>
    ///     Enumerates the installed GGUF models (served by the bundled llama.cpp runtime, independent of Ollama). A
    ///     best-effort read: any failure (e.g. an unreadable registry) yields an empty list rather than failing the
    ///     whole catalog, so the Ollama path's availability is unaffected.
    /// </summary>
    private async Task<IReadOnlyList<LocalModelDescriptor>> ResolveInstalledGgufModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _ggufModelStore.ListInstalledModelsAsync(cancellationToken).ConfigureAwait(false);
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
    ///     True when a stored Codex session exists whose access token is non-expired (skew-adjusted — the same gate
    ///     <c>cloud/codex/status</c> uses). A best-effort read: any failure resolving the session offers no Codex
    ///     models rather than failing the whole catalog.
    /// </summary>
    private async Task<bool> HasUsableCodexSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var session = await _codexTokenStore.LoadAsync(cancellationToken).ConfigureAwait(false);
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

    private static string ReadModelName(Model model) =>
        !string.IsNullOrWhiteSpace(model.ModelName)
            ? model.ModelName
            : model.Name ?? string.Empty;
}
