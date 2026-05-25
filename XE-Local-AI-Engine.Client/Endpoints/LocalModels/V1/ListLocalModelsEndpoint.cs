namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

public sealed class ListLocalModelsEndpoint(
    IOllamaModelService modelService,
    INodeSettingsStore nodeSettingsStore,
    IOptions<LocalChatAgentOptions> localChatOptions,
    ILogger<ListLocalModelsEndpoint> logger) : EndpointWithoutRequest<ListLocalModelsResponse>
{
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
            var models = await _modelService.ListLocalModelsAsync(ct).ConfigureAwait(false);
            var response = new ListLocalModelsResponse
            {
                IsAvailable = true,
                SelectedModelName = selectedModelName,
                ConfiguredDefaultModelName = _localChatOptions.Value.DefaultModel,
                Items = models
                        .Where(static model => !string.IsNullOrWhiteSpace(model.ModelName) || !string.IsNullOrWhiteSpace(model.Name))
                        .Select(model => model.ToResponse(selectedModelName))
                        .OrderBy(static model => model.ModelName, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
            };

            await Send.OkAsync(response, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Local model list could not be loaded.");
            await Send.OkAsync(new ListLocalModelsResponse
            {
                IsAvailable = false,
                SelectedModelName = selectedModelName,
                ConfiguredDefaultModelName = _localChatOptions.Value.DefaultModel,
                Error = "Local model provider is unavailable.",
                Items = []
            }, ct).ConfigureAwait(false);
        }
    }
}
