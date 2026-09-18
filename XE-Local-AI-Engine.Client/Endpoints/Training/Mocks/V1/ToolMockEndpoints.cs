namespace XE_Local_AI_Engine.Client.Endpoints.Training.Mocks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

public sealed class ListToolMocksEndpoint : EndpointWithoutRequest<ListToolMocksResponse>
{
    private readonly IToolMockService _mocks;

    public ListToolMocksEndpoint(IToolMockService mocks)
    {
        ArgumentNullException.ThrowIfNull(mocks);
        _mocks = mocks;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.Mocks);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var records = await _mocks.ListAsync(ct);
        await Send.OkAsync(new ListToolMocksResponse
        {
            Items = records.Select(record => record.ToResponse()).ToArray()
        }, ct);
    }
}

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
        var record = await _mocks.CreateAsync(new ToolMockDraft(req.ToolName, req.Body, req.Enabled), ct);
        await Send.CreatedAtAsync<GetToolMockEndpoint>(new
                  {
                      mockId = record.Id
                  }, record.ToResponse(), cancellation: ct);
    }
}

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
        var record = await _mocks.UpdateAsync(req.MockId, req.ExpectedVersion, new ToolMockDraft(req.ToolName, req.Body, req.Enabled), ct);
        await Send.OkAsync(record.ToResponse(), ct);
    }
}

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
