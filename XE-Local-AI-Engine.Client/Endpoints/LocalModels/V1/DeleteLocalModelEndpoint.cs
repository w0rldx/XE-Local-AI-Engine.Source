namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.LlamaServer;

public sealed class DeleteLocalModelEndpoint(
    ILocalModelDeletionCoordinator deletionCoordinator,
    ILocalModelProviderResolver providerResolver,
    ModelNameValidator modelNameValidator) : Endpoint<DeleteLocalModelRequest, DeleteLocalModelResponse>
{
    private readonly ILocalModelDeletionCoordinator _deletionCoordinator = deletionCoordinator ?? throw new ArgumentNullException(nameof(deletionCoordinator));
    private readonly ILocalModelProviderResolver _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));

    public override void Configure()
    {
        Delete(LocalApiRoutes.LocalModels.ModelByName);
        Policies(NodeAuthorizationPolicies.Operator);

        // The coordinator's three refusals (dependent adapters, provider conflict, superseded provider map) are typed
        // conflict exceptions the global ConflictExceptionHandler turns into this envelope — never caught here.
        Description(builder => builder
                               .Produces<DeleteLocalModelResponse>(StatusCodes.Status200OK)
                               .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(DeleteLocalModelRequest req, CancellationToken ct)
    {
        // Decode FIRST: the bound route value may still contain literal %2F (see ModelRouteName), so validate and delete
        // the decoded canonical name to keep "validated name == deleted name" true.
        var decodedModelName = ModelRouteName.Decode(req.ModelName);
        if (!await ValidateModelNameAsync(decodedModelName, ct).ConfigureAwait(false))
        {
            return;
        }

        var modelName = decodedModelName!.Trim();

        var providerName = await _providerResolver.ResolveProviderNameForModelAsync(modelName, ct).ConfigureAwait(false);
        CommittedModelDeletion? committed = null;
        if (string.Equals(providerName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                committed = await _deletionCoordinator.CommitDeleteAsync(modelName, ct).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                // Delete is idempotent: an already-ejected/uninstalled model still reports success rather than 500ing.
            }
        }
        else
        {
            await _providerResolver.ResolveProvider(providerName).DeleteModelAsync(modelName, ct).ConfigureAwait(false);
            _providerResolver.InvalidateModelProviderMap();
        }

        await Send.OkAsync(new DeleteLocalModelResponse
        {
            ModelName = modelName,
            Deleted = true
        }, ct).ConfigureAwait(false);
        if (committed is not null)
        {
            await _deletionCoordinator.PurgeAfterSuccessAsync(committed, CancellationToken.None).ConfigureAwait(false);
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
