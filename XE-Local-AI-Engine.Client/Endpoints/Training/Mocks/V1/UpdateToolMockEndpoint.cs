namespace XE_Local_AI_Engine.Client.Endpoints.Training.Mocks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

public sealed class UpdateToolMockEndpoint : Endpoint<UpdateToolMockRequest, ToolMockResponse>
{
    private readonly IToolMockService _mocks;

    public UpdateToolMockEndpoint(IToolMockService mocks)
    {
        ArgumentNullException.ThrowIfNull(mocks);
        _mocks = mocks;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.Training.MockById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(UpdateToolMockRequest req, CancellationToken ct)
    {
        var record = await _mocks.UpdateAsync(req.MockId, req.ExpectedVersion, new ToolMockDraft { ToolName = req.ToolName, Body = req.Body, Enabled = req.Enabled }, ct);
        await Send.OkAsync(record.ToResponse(), ct);
    }
}
