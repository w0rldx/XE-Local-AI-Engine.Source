namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class DeleteModelKindEndpoint : Endpoint<ResetModelKindRequest, ModelKindResponse>
{
    private readonly IModelClassificationService _classificationService;

    public DeleteModelKindEndpoint(IModelClassificationService classificationService)
    {
        ArgumentNullException.ThrowIfNull(classificationService);
        _classificationService = classificationService;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.LocalModels.ModelKind);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ResetModelKindRequest req, CancellationToken ct)
    {
        // Decode again here: ResetModelKindRequestValidator already ran the grammar over the decoded name, and the
        // name that is reset must be that same decoded one. See ModelRouteName.
        var decodedModelName = ModelRouteName.Decode(req.ModelName);

        // The validator's pattern rejects whitespace, so the validated (decoded) name is already the persisted key — pass it
        // through unchanged so the key that was validated and the key that is reset are provably identical.
        var result = await _classificationService.ResetOverrideAsync(decodedModelName!, ct);
        await Send.OkAsync(result.ToKindResponse(), ct);
    }
}
