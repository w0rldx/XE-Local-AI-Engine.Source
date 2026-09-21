namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class PutModelKindEndpoint : Endpoint<SetModelKindRequest, ModelKindResponse>
{
    private readonly IModelClassificationService _classificationService;

    public PutModelKindEndpoint(
        IModelClassificationService classificationService)
    {
        ArgumentNullException.ThrowIfNull(classificationService);
        _classificationService = classificationService;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.LocalModels.ModelKind);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SetModelKindRequest req, CancellationToken ct)
    {
        // Decode again here: SetModelKindRequestValidator already ran the grammar over the decoded name, and the
        // name that is stored must be that same decoded one. See ModelRouteName.
        var decodedModelName = ModelRouteName.Decode(req.ModelName);

        // SetModelKindRequestValidator already refused an undefined kind, so this parse cannot fail — it reads the
        // same helper the validator read, which is what makes "the accepted kind is the stored kind" provable.
        _ = LocalModelsMapper.TryParseKind(req.Kind, out var kind);

        // The validator's pattern rejects whitespace, so the validated (decoded) name is already the persisted key — pass it
        // through unchanged so the key that was validated and the key that is stored/probed are provably identical.
        var result = await _classificationService.SetOverrideAsync(decodedModelName!, kind, ct);
        await Send.OkAsync(result.ToKindResponse(), ct);
    }
}
