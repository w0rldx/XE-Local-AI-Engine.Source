namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Validation;

public sealed class SelectLocalModelEndpoint(
    INodeSettingsStore nodeSettingsStore,
    ICloudModelResolver cloudModelResolver,
    IActiveCloudChatClientFactory activeCloudChatClientFactory,
    ModelNameValidator modelNameValidator) : Endpoint<SelectLocalModelRequest, SelectLocalModelResponse>
{
    private readonly IActiveCloudChatClientFactory _activeCloudChatClientFactory = activeCloudChatClientFactory ?? throw new ArgumentNullException(nameof(activeCloudChatClientFactory));
    private readonly ICloudModelResolver _cloudModelResolver = cloudModelResolver ?? throw new ArgumentNullException(nameof(cloudModelResolver));
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));
    private readonly INodeSettingsStore _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));

    public override void Configure()
    {
        Post(LocalApiRoutes.LocalModels.Select);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SelectLocalModelRequest req, CancellationToken ct)
    {
        if (!await ValidateModelNameAsync(req.ModelName, ct).ConfigureAwait(false))
        {
            return;
        }

        var selectedModelName = req.ModelName!.Trim();
        var settings = await _nodeSettingsStore.LoadAsync(ct).ConfigureAwait(false);
        var previousModelName = settings.DefaultModelName;
        await _nodeSettingsStore.SaveAsync(settings with
        {
            DefaultModelName = selectedModelName
        }, ct).ConfigureAwait(false);

        // Azure routing is selected-model-driven and the active-cloud selection is snapshot-cached for a few seconds.
        // When either the previous OR the new selection is a cloud model (a Codex id or a stored Azure
        // deployment), invalidate that snapshot so an Azure↔local / Azure-A↔Azure-B / Codex switch takes effect on the
        // next send instead of after the TTL. A local→local switch leaves the cloud snapshot untouched (no cloud client
        // is involved either way).
        if (await _cloudModelResolver.IsCloudModelAsync(previousModelName, ct).ConfigureAwait(false)
            || await _cloudModelResolver.IsCloudModelAsync(selectedModelName, ct).ConfigureAwait(false))
        {
            _activeCloudChatClientFactory.InvalidateSelectionCache();
        }

        await Send.OkAsync(new SelectLocalModelResponse
        {
            SelectedModelName = selectedModelName
        }, ct).ConfigureAwait(false);
    }

    private async Task<bool> ValidateModelNameAsync(string? modelName, CancellationToken ct)
    {
        var validationError = _modelNameValidator.GetValidationError(modelName);
        if (validationError is null)
        {
            return true;
        }

        AddError(validationError);
        await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        return false;
    }
}
