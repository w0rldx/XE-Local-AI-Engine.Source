namespace XE_Local_AI_Engine.Client.Endpoints.Training.Mocks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

public sealed class CreateToolMockEndpoint : Endpoint<CreateToolMockRequest, ToolMockResponse>
{
    private readonly IToolMockService _mocks;

    public CreateToolMockEndpoint(IToolMockService mocks)
    {
        ArgumentNullException.ThrowIfNull(mocks);
        _mocks = mocks;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.Mocks);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreateToolMockRequest req, CancellationToken ct)
    {
        var record = await _mocks.CreateAsync(new ToolMockDraft { ToolName = req.ToolName, Body = req.Body, Enabled = req.Enabled }, ct);
        await Send.CreatedAtAsync<GetToolMockEndpoint>(new
                  {
                      mockId = record.Id
                  }, record.ToResponse(), cancellation: ct);
    }
}
