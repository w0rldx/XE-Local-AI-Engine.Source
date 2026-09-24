namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Models;

public sealed class DeleteLocalModelEndpoint : Endpoint<DeleteLocalModelRequest, DeleteLocalModelResponse>
{
    private readonly ILocalModelAdministrationService _administrationService;

    public DeleteLocalModelEndpoint(ILocalModelAdministrationService administrationService)
    {
        ArgumentNullException.ThrowIfNull(administrationService);
        _administrationService = administrationService;
    }

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
        // Decode again here: DeleteLocalModelRequestValidator already ran the grammar over the decoded name, and
        // deleting the same decoded name keeps "validated name == deleted name" true. See ModelRouteName.
        var decodedModelName = ModelRouteName.Decode(req.ModelName);
        var result = await _administrationService.DeleteAsync(decodedModelName, ct);

        await Send.OkAsync(new DeleteLocalModelResponse
        {
            ModelName = result.ModelName!,
            Deleted = result.Deleted
        }, ct);
    }
}
