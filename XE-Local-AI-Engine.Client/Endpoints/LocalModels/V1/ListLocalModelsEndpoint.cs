namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Options;

public sealed class ListLocalModelsEndpoint(
    IOllamaModelService modelService,
    IModelClassificationService classificationService,
    IGgufModelStore ggufModelStore,
    INodeRuntimeSettings runtimeSettings,
    IOptions<LocalChatAgentOptions> localChatOptions,
    ICodexTokenStore codexTokenStore,
    IOptions<CodexOptions> codexOptions,
    TimeProvider timeProvider,
    ILogger<ListLocalModelsEndpoint> logger) : EndpointWithoutRequest<ListLocalModelsResponse>
{
    private readonly IModelClassificationService _classificationService = classificationService ?? throw new ArgumentNullException(nameof(classificationService));
    private readonly CodexOptions _codexOptions = (codexOptions ?? throw new ArgumentNullException(nameof(codexOptions))).Value;
    private readonly ICodexTokenStore _codexTokenStore = codexTokenStore ?? throw new ArgumentNullException(nameof(codexTokenStore));
    private readonly IGgufModelStore _ggufModelStore = ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));
    private readonly IOptions<LocalChatAgentOptions> _localChatOptions = localChatOptions ?? throw new ArgumentNullException(nameof(localChatOptions));
    private readonly ILogger<ListLocalModelsEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IOllamaModelService _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
    private readonly INodeRuntimeSettings _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalModels.Models);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // The effective selected model resolves through the accessor (stored DefaultModelName > appsettings
        // Agent:LocalChat:DefaultModel seed); the configured-default arg below stays the appsettings seed so the picker
        // can still surface "node default" distinctly from the operator's selection.
        var selectedModelName = await _runtimeSettings.GetDefaultModelNameAsync(ct).ConfigureAwait(false);

        // Codex cloud models are offered only when a usable (non-expired) Codex session is present. They do not
        // depend on the local Ollama runtime, so they are resolved up front and included even when Ollama is
        // unavailable below.
        var cloudModels = await ResolveCodexCloudModelsAsync(selectedModelName, ct).ConfigureAwait(false);

        // Installed GGUF models are served by the bundled llama.cpp runtime, NOT Ollama — resolve them up front (like
        // cloud models) so they are included even when Ollama is absent and the call below throws. A best-effort read:
        // any failure enumerating the GGUF store yields no GGUF entries rather than failing the whole list.
        var ggufModels = await ResolveInstalledGgufModelsAsync(ct).ConfigureAwait(false);

        try
        {
            var models = (await _modelService.ListLocalModelsAsync(ct).ConfigureAwait(false)).ToList();

            // Lazily resolve each model's effective kind, caching detection by content digest. A cache hit issues no
            // /api/show call, so repeated list calls are cheap.
            var classifications = await _classificationService
                                        .ClassifyAsync(models.Select(static model => (model.ReadModelName(), (string?)model.Digest)), ct)
                                        .ConfigureAwait(false);

            var response = LocalModelsMapper.ToListResponse(models, selectedModelName, _localChatOptions.Value.DefaultModel, classifications, cloudModels, ggufModels);

            await Send.OkAsync(response, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // An unreachable Ollama endpoint (HttpRequestException) is expected in desktop mode and degrades cleanly to
            // the installed-GGUF + cloud list below — log it at Debug so it doesn't flood the console. Any OTHER failure
            // is unexpected and stays at Warning.
            if (exception is HttpRequestException)
            {
                _logger.LogDebug(exception, "Ollama not reachable while loading the model list; returning installed GGUF/cloud models only.");
            }
            else
            {
                _logger.LogWarning(exception, "Local model list could not be loaded.");
            }

            await Send.OkAsync(LocalModelsMapper.ToUnavailableListResponse(selectedModelName,
                    _localChatOptions.Value.DefaultModel,
                    "Local model provider is unavailable.",
                    cloudModels,
                    ggufModels),
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Enumerates the installed GGUF models (served by the bundled llama.cpp runtime, independent of Ollama). A
    ///     best-effort read: any failure (e.g. an unreadable registry) yields an empty list rather than failing the
    ///     whole model list, so the Ollama path's availability is unaffected.
    /// </summary>
    private async Task<IReadOnlyList<LocalModelDescriptor>> ResolveInstalledGgufModelsAsync(CancellationToken ct)
    {
        try
        {
            return await _ggufModelStore.ListInstalledModelsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
    ///     Returns the Codex cloud model entries when a usable Codex session exists (a stored session whose access
    ///     token is non-expired, skew-adjusted — the same gate <c>cloud/codex/status</c> uses), otherwise an empty
    ///     list. A best-effort read: any failure resolving the session yields no cloud models rather than failing the
    ///     whole list.
    /// </summary>
    private async Task<IReadOnlyList<LocalModelResponse>> ResolveCodexCloudModelsAsync(string? selectedModelName, CancellationToken ct)
    {
        try
        {
            var session = await _codexTokenStore.LoadAsync(ct).ConfigureAwait(false);
            if (session is null || session.IsExpired(_codexOptions.ExpirySkew, _timeProvider.GetUtcNow()))
            {
                return [];
            }

            return LocalModelsMapper.ToCodexCloudModelResponses(selectedModelName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Codex cloud model list could not be resolved.");
            return [];
        }
    }
}
