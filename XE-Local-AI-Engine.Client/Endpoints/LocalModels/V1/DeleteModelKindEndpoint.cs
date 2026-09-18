namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Validation;

public sealed class DeleteModelKindEndpoint : Endpoint<ResetModelKindRequest, ModelKindResponse>
{
    private readonly IModelClassificationService _classificationService;
    private readonly ModelNameValidator _modelNameValidator;

    public DeleteModelKindEndpoint(
        IModelClassificationService classificationService,
        ModelNameValidator modelNameValidator)
    {
        ArgumentNullException.ThrowIfNull(classificationService);
        ArgumentNullException.ThrowIfNull(modelNameValidator);
        _classificationService = classificationService;
        _modelNameValidator = modelNameValidator;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.LocalModels.ModelKind);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ResetModelKindRequest req, CancellationToken ct)
    {
        // Decode FIRST: the bound route value may still contain literal %2F (see ModelRouteName), so validate and reset
        // the decoded canonical name.
        var decodedModelName = ModelRouteName.Decode(req.ModelName);
        var validationError = _modelNameValidator.GetValidationError(decodedModelName);
        if (validationError is not null)
        {
            AddError(validationError);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        // The validator's pattern rejects whitespace, so the validated (decoded) name is already the persisted key — pass it
        // through unchanged so the key that was validated and the key that is reset are provably identical.
        var result = await _classificationService.ResetOverrideAsync(decodedModelName!, ct);
        await Send.OkAsync(result.ToKindResponse(), ct);
    }
}
