namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;

public sealed class SelectLocalModelEndpoint(
    INodeSettingsStore nodeSettingsStore,
    ICloudCredentialStore cloudCredentialStore,
    IActiveCloudChatClientFactory activeCloudChatClientFactory,
    ModelNameValidator modelNameValidator) : Endpoint<SelectLocalModelRequest, SelectLocalModelResponse>
{
    private readonly IActiveCloudChatClientFactory _activeCloudChatClientFactory = activeCloudChatClientFactory ?? throw new ArgumentNullException(nameof(activeCloudChatClientFactory));
    private readonly ICloudCredentialStore _cloudCredentialStore = cloudCredentialStore ?? throw new ArgumentNullException(nameof(cloudCredentialStore));
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

        // Azure routing is selected-model-driven and the active-cloud selection is snapshot-cached for a few seconds
        // (MEDIUM-2). When either the previous OR the new selection is a cloud model (a Codex id or a stored Azure
        // deployment), invalidate that snapshot so an Azure↔local / Azure-A↔Azure-B / Codex switch takes effect on the
        // next send instead of after the TTL. A local→local switch leaves the cloud snapshot untouched (no cloud client
        // is involved either way).
        if (await IsCloudModelAsync(previousModelName, ct).ConfigureAwait(false)
            || await IsCloudModelAsync(selectedModelName, ct).ConfigureAwait(false))
        {
            _activeCloudChatClientFactory.InvalidateSelectionCache();
        }

        await Send.OkAsync(new SelectLocalModelResponse
        {
            SelectedModelName = selectedModelName
        }, ct).ConfigureAwait(false);
    }

    // True when the model id is a cloud model — a Codex catalog id or a stored Azure Foundry deployment name. Best-effort
    // on the Azure read: a config-resolution failure treats the id as non-cloud (the worst case is the selection snapshot
    // simply expires on its own short TTL rather than invalidating immediately).
    private async Task<bool> IsCloudModelAsync(string? modelName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        if (CodexModelCatalog.IsCodexModel(modelName))
        {
            return true;
        }

        try
        {
            var config = await _cloudCredentialStore.LoadConfigAsync(ct).ConfigureAwait(false);
            var connection = config?.AzureFoundry;
            return connection is { Models.Count: > 0 }
                   && connection.Models.Any(model => string.Equals(model.DeploymentName, modelName, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Logger.LogDebug(exception, "Azure Foundry deployment match could not be resolved for '{ModelName}'.", modelName);
            return false;
        }
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
