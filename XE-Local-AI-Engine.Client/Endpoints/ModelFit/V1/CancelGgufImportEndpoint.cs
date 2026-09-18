namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

public sealed class CancelGgufImportEndpoint : Endpoint<GgufImportOperationRequest, CancelGgufImportResponse>
{
    private readonly IGgufImportTransactionCoordinator _coordinator;

    public CancelGgufImportEndpoint(IGgufImportTransactionCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.ImportCancel);
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

        var cancelled = _coordinator.Cancel(req.OperationId);
        await Send.OkAsync(new CancelGgufImportResponse
        {
            OperationId = req.OperationId,
            CancellationRequested = cancelled,
            Status = GgufImportEndpointSupport.Map(_coordinator.GetStatus(req.OperationId) ?? status)
        }, ct);
    }
}
