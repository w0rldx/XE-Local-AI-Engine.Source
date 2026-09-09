namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Validation;

/// <summary>
///     Reports one model's details. The provider routing behind "which model is this and what are its details" lives in
///     <see cref="ILocalModelDetailsResolver" />; this endpoint binds, delegates, and maps the one resolution it gets
///     back — a <see cref="LocalModelDetailsResolution.NoLocalDetails" /> is the single 404 for every branch that has
///     no local details to report.
/// </summary>
public sealed class GetLocalModelDetailsEndpoint(
    ILocalModelDetailsResolver detailsResolver,
    ModelNameValidator modelNameValidator) : Endpoint<GetLocalModelDetailsRequest, LocalModelDetailsResponse>
{
    private readonly ILocalModelDetailsResolver _detailsResolver = detailsResolver ?? throw new ArgumentNullException(nameof(detailsResolver));
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalModels.ModelDetails);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetLocalModelDetailsRequest req, CancellationToken ct)
    {
        // Decode FIRST: the bound route value may still contain literal %2F (see ModelRouteName), so validate and probe
        // the decoded canonical name to keep "validated name == probed name" true.
        var decodedModelName = ModelRouteName.Decode(req.ModelName);
        if (!await ValidateModelNameAsync(decodedModelName, ct).ConfigureAwait(false))
        {
            return;
        }

        var modelName = decodedModelName!.Trim();
        var resolution = await _detailsResolver.ResolveAsync(modelName, ct).ConfigureAwait(false);

        switch (resolution)
        {
            case LocalModelDetailsResolution.External external:
                await Send.OkAsync(external.Registration.ToDetailsResponse(), ct).ConfigureAwait(false);
                return;
            case LocalModelDetailsResolution.Gguf gguf:
                await Send.OkAsync(gguf.Descriptor.ToDetailsResponse(modelName, gguf.EffectiveContextTokens), ct).ConfigureAwait(false);
                return;
            case LocalModelDetailsResolution.Ollama ollama:
                await Send.OkAsync(ollama.Details.ToResponse(modelName), ct).ConfigureAwait(false);
                return;
            default:
                await Send.NotFoundAsync(ct).ConfigureAwait(false);
                return;
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
