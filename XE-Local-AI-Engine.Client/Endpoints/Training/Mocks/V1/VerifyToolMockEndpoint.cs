namespace XE_Local_AI_Engine.Client.Endpoints.Training.Mocks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

/// <summary>Runs the static verifier and records its verdict. A failing verdict also disables the mock.</summary>
public sealed class VerifyToolMockEndpoint : Endpoint<VerifyToolMockRequest, ToolMockResponse>
{
    private readonly IToolMockService _mocks;

    public VerifyToolMockEndpoint(IToolMockService mocks)
    {
        ArgumentNullException.ThrowIfNull(mocks);
        _mocks = mocks;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.MockVerify);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(VerifyToolMockRequest req, CancellationToken ct)
    {
        var result = await _mocks.VerifyAsync(req.MockId, req.ExpectedVersion, ct);
        await Send.OkAsync(result.Mock.ToResponse(), ct);
    }
}
