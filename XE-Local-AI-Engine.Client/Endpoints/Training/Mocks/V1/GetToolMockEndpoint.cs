namespace XE_Local_AI_Engine.Client.Endpoints.Training.Mocks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

public sealed class GetToolMockEndpoint : Endpoint<GetToolMockRequest, ToolMockResponse>
{
    private readonly IToolMockService _mocks;

    public GetToolMockEndpoint(IToolMockService mocks)
    {
        ArgumentNullException.ThrowIfNull(mocks);
        _mocks = mocks;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.MockById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetToolMockRequest req, CancellationToken ct)
    {
        var record = await _mocks.GetAsync(req.MockId, ct);
        if (record is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct);
    }
}
