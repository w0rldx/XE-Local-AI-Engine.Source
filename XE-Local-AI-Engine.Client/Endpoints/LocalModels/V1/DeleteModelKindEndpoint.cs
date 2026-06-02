namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Validation;

public sealed class DeleteModelKindEndpoint(
    IModelClassificationService classificationService,
    ModelNameValidator modelNameValidator) : Endpoint<ResetModelKindRequest, ModelKindResponse>
{
    private readonly IModelClassificationService _classificationService = classificationService ?? throw new ArgumentNullException(nameof(classificationService));
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));

    public override void Configure()
    {
        Delete(LocalApiRoutes.LocalModels.ModelKind);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ResetModelKindRequest req, CancellationToken ct)
    {
        var validationError = _modelNameValidator.GetValidationError(req.ModelName);
        if (validationError is not null)
        {
            AddError(validationError);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        // The validator's pattern rejects whitespace, so the validated name is already the persisted key — pass it through
        // unchanged so the key that was validated and the key that is reset are provably identical.
        var result = await _classificationService.ResetOverrideAsync(req.ModelName!, ct).ConfigureAwait(false);
        await Send.OkAsync(result.ToKindResponse(), ct).ConfigureAwait(false);
    }
}
