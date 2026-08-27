namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.External;

public sealed class SelectLocalModelEndpoint(
    ILocalModelAdministrationService administrationService,
    IModelTrustResolver modelTrustResolver,
    ModelNameValidator modelNameValidator) : Endpoint<SelectLocalModelRequest, SelectLocalModelResponse>
{
    private readonly ILocalModelAdministrationService _administrationService = administrationService ?? throw new ArgumentNullException(nameof(administrationService));
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));
    private readonly IModelTrustResolver _modelTrustResolver = modelTrustResolver ?? throw new ArgumentNullException(nameof(modelTrustResolver));

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

        if (!await ValidateExternalRegistrationAsync(req.ModelName, ct).ConfigureAwait(false))
        {
            return;
        }

        var result = await _administrationService
                           .SelectDefaultAsync(req.ModelName, LocalModelSelectionPolicy.ConfiguredModel, ct)
                           .ConfigureAwait(false);

        await Send.OkAsync(new SelectLocalModelResponse
        {
            SelectedModelName = result.SelectedModelName!
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

    /// <summary>
    ///     Refuses an <c>ext:</c> id that no configured connection actually serves.
    /// </summary>
    /// <remarks>
    ///     A well-formed id whose registration is gone passes the name validator — the grammar is all that check knows —
    ///     and storing it would make it the node default, from which every chat turn would fail to route with no
    ///     explanation of why. The reconciliation pass clears such a default when it finds one at startup; this stops
    ///     one being written in the first place. Only external ids are checked here: an unknown id under any OTHER
    ///     provider is a legitimate selection (a model still downloading, an Ollama tag pulled later), and this endpoint
    ///     has never claimed to verify installation.
    /// </remarks>
    private async Task<bool> ValidateExternalRegistrationAsync(string? modelName, CancellationToken ct)
    {
        if (!ExternalModelId.HasExternalScheme(modelName))
        {
            return true;
        }

        var registration = await _modelTrustResolver.TryResolveExternalAsync(modelName, ct).ConfigureAwait(false);
        if (registration is not null)
        {
            return true;
        }

        AddError("No external connection registers that model. Refresh the model list and select it again.");
        await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        return false;
    }
}
