namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

public sealed class GetGgufImportStatusEndpoint : Endpoint<GgufImportOperationRequest, GgufAcquisitionStatusResponse>
{
    private readonly IGgufImportTransactionCoordinator _coordinator;

    public GetGgufImportStatusEndpoint(IGgufImportTransactionCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.ImportStatus);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GgufImportOperationRequest req, CancellationToken ct)
    {
        var status = _coordinator.GetStatus(req.OperationId);
        if (status is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(GgufImportEndpointSupport.Map(status), ct);
    }
}
