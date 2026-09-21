namespace XE_Local_AI_Engine.Client.Endpoints.Training.Mocks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

public sealed class DeleteToolMockEndpoint : Endpoint<DeleteToolMockRequest>
{
    private readonly IToolMockService _mocks;

    public DeleteToolMockEndpoint(IToolMockService mocks)
    {
        ArgumentNullException.ThrowIfNull(mocks);
        _mocks = mocks;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Training.MockById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeleteToolMockRequest req, CancellationToken ct)
    {
        await _mocks.DeleteAsync(req.MockId, req.ExpectedVersion, ct);
        await Send.NoContentAsync(ct);
    }
}
