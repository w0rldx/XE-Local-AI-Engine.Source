namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Validation;

/// <summary>
///     FastEndpoints handler for setting the operator override of a local model's classification.
/// </summary>
public sealed class PutModelKindEndpoint(
    IModelClassificationService classificationService,
    ModelNameValidator modelNameValidator) : Endpoint<SetModelKindRequest, ModelKindResponse>
{
    private readonly IModelClassificationService _classificationService = classificationService ?? throw new ArgumentNullException(nameof(classificationService));
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));

    public override void Configure()
    {
        Put(LocalApiRoutes.LocalModels.ModelKind);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SetModelKindRequest req, CancellationToken ct)
    {
        var validationError = _modelNameValidator.GetValidationError(req.ModelName);
        if (validationError is not null)
        {
            AddError(validationError);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        if (!TryParseKind(req.Kind, out var kind))
        {
            AddError("Invalid model kind");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        // The validator's pattern rejects whitespace, so the validated name is already the persisted key — pass it through
        // unchanged so the key that was validated and the key that is stored/probed are provably identical.
        var result = await _classificationService.SetOverrideAsync(req.ModelName!, kind, ct).ConfigureAwait(false);
        await Send.OkAsync(result.ToKindResponse(), ct).ConfigureAwait(false);
    }

    private static bool TryParseKind(string? value, out ModelKind kind)
    {
        kind = ModelKind.Unknown;

        // Enum.TryParse accepts numeric strings for undefined values (e.g. "99"), so guard with Enum.IsDefined to only
        // allow the defined ModelKind names. Setting Unknown is allowed as an explicit "I don't know" override.
        return !string.IsNullOrWhiteSpace(value)
            && Enum.TryParse(value, ignoreCase: true, out kind)
            && Enum.IsDefined(kind);
    }
}
