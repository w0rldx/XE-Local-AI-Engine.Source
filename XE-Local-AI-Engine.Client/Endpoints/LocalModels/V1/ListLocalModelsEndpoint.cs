namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;

public sealed class ListLocalModelsEndpoint(
    IOllamaModelService modelService,
    IModelClassificationService classificationService,
    INodeSettingsStore nodeSettingsStore,
    IOptions<LocalChatAgentOptions> localChatOptions,
    ICodexTokenStore codexTokenStore,
    IOptions<CodexOptions> codexOptions,
    TimeProvider timeProvider,
    ILogger<ListLocalModelsEndpoint> logger) : EndpointWithoutRequest<ListLocalModelsResponse>
{
    private readonly IModelClassificationService _classificationService = classificationService ?? throw new ArgumentNullException(nameof(classificationService));
    private readonly IOptions<LocalChatAgentOptions> _localChatOptions = localChatOptions ?? throw new ArgumentNullException(nameof(localChatOptions));
    private readonly ILogger<ListLocalModelsEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IOllamaModelService _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
    private readonly INodeSettingsStore _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));
    private readonly ICodexTokenStore _codexTokenStore = codexTokenStore ?? throw new ArgumentNullException(nameof(codexTokenStore));
    private readonly CodexOptions _codexOptions = (codexOptions ?? throw new ArgumentNullException(nameof(codexOptions))).Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalModels.Models);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var settings = await _nodeSettingsStore.LoadAsync(ct).ConfigureAwait(false);
        var selectedModelName = settings.DefaultModelName ?? _localChatOptions.Value.DefaultModel;

        // Codex cloud models are offered only when a usable (non-expired) Codex session is present. They do not
        // depend on the local Ollama runtime, so they are resolved up front and included even when Ollama is
        // unavailable below.
        var cloudModels = await ResolveCodexCloudModelsAsync(selectedModelName, ct).ConfigureAwait(false);

        try
        {
            var models = (await _modelService.ListLocalModelsAsync(ct).ConfigureAwait(false)).ToList();

            // Lazily resolve each model's effective kind, caching detection by content digest. A cache hit issues no
            // /api/show call, so repeated list calls are cheap.
            var classifications = await _classificationService
                                        .ClassifyAsync(models.Select(static model => (model.ReadModelName(), (string?)model.Digest)), ct)
                                        .ConfigureAwait(false);

            var response = LocalModelsMapper.ToListResponse(models, selectedModelName, _localChatOptions.Value.DefaultModel, classifications, cloudModels);

            await Send.OkAsync(response, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Local model list could not be loaded.");
            await Send.OkAsync(LocalModelsMapper.ToUnavailableListResponse(selectedModelName,
                    _localChatOptions.Value.DefaultModel,
                    "Local model provider is unavailable.",
                    cloudModels),
                ct).ConfigureAwait(false);
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
