namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

public sealed class ListLocalModelsEndpoint(
    IOllamaModelService modelService,
    IModelClassificationService classificationService,
    INodeSettingsStore nodeSettingsStore,
    IOptions<LocalChatAgentOptions> localChatOptions,
    ILogger<ListLocalModelsEndpoint> logger) : EndpointWithoutRequest<ListLocalModelsResponse>
{
    private readonly IModelClassificationService _classificationService = classificationService ?? throw new ArgumentNullException(nameof(classificationService));
    private readonly IOptions<LocalChatAgentOptions> _localChatOptions = localChatOptions ?? throw new ArgumentNullException(nameof(localChatOptions));
    private readonly ILogger<ListLocalModelsEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IOllamaModelService _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
    private readonly INodeSettingsStore _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalModels.Models);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var settings = await _nodeSettingsStore.LoadAsync(ct).ConfigureAwait(false);
        var selectedModelName = settings.DefaultModelName ?? _localChatOptions.Value.DefaultModel;

        try
        {
            var models = (await _modelService.ListLocalModelsAsync(ct).ConfigureAwait(false)).ToList();

            // Lazily resolve each model's effective kind, caching detection by content digest. A cache hit issues no
            // /api/show call, so repeated list calls are cheap.
            var classifications = await _classificationService
                .ClassifyAsync(models.Select(static model => (model.ReadModelName(), (string?)model.Digest)), ct)
                .ConfigureAwait(false);

            var response = LocalModelsMapper.ToListResponse(models, selectedModelName, _localChatOptions.Value.DefaultModel, classifications);

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
                    "Local model provider is unavailable."),
                ct).ConfigureAwait(false);
        }
    }
}
