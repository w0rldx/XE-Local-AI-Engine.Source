namespace XE_Local_AI_Engine.Client.Endpoints.Training.Mocks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

public sealed class ListToolMocksEndpoint(ITrainingDatasetStore store)
    : EndpointWithoutRequest<ListToolMocksResponse>
{
    private readonly ITrainingDatasetStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.Mocks);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var records = await _store.ListMocksAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListToolMocksResponse
        {
            Items = records.Select(record => record.ToResponse()).ToArray()
        }, ct).ConfigureAwait(false);
    }
}

public sealed class GetToolMockEndpoint(ITrainingDatasetStore store)
    : Endpoint<GetToolMockRequest, ToolMockResponse>
{
    private readonly ITrainingDatasetStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.MockById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetToolMockRequest req, CancellationToken ct)
    {
        var record = await _store.GetMockAsync(req.MockId, ct).ConfigureAwait(false);
        if (record is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
    }
}

public sealed class CreateToolMockEndpoint(IToolMockService mocks)
    : Endpoint<CreateToolMockRequest, ToolMockResponse>
{
    private readonly IToolMockService _mocks = mocks ?? throw new ArgumentNullException(nameof(mocks));

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.Mocks);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreateToolMockRequest req, CancellationToken ct)
    {
        var record = await _mocks.CreateAsync(new ToolMockDraft(req.ToolName, req.Body, req.Enabled), ct).ConfigureAwait(false);
        await Send.CreatedAtAsync<GetToolMockEndpoint>(new
                  {
                      mockId = record.Id
                  }, record.ToResponse(), cancellation: ct)
                  .ConfigureAwait(false);
    }
}

public sealed class UpdateToolMockEndpoint(IToolMockService mocks)
    : Endpoint<UpdateToolMockRequest, ToolMockResponse>
{
    private readonly IToolMockService _mocks = mocks ?? throw new ArgumentNullException(nameof(mocks));

    public override void Configure()
    {
        Put(LocalApiRoutes.Training.MockById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(UpdateToolMockRequest req, CancellationToken ct)
    {
        var record = await _mocks.UpdateAsync(req.MockId, req.ExpectedVersion, new ToolMockDraft(req.ToolName, req.Body, req.Enabled), ct)
                                 .ConfigureAwait(false);
        await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
    }
}

public sealed class DeleteToolMockEndpoint(ITrainingDatasetStore store)
    : Endpoint<DeleteToolMockRequest>
{
    private readonly ITrainingDatasetStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Training.MockById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeleteToolMockRequest req, CancellationToken ct)
    {
        await _store.DeleteMockAsync(req.MockId, req.ExpectedVersion, ct).ConfigureAwait(false);
        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Runs the static verifier and records its verdict. A failing verdict also disables the mock.</summary>
public sealed class VerifyToolMockEndpoint(IToolMockService mocks)
    : Endpoint<VerifyToolMockRequest, ToolMockResponse>
{
    private readonly IToolMockService _mocks = mocks ?? throw new ArgumentNullException(nameof(mocks));

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.MockVerify);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(VerifyToolMockRequest req, CancellationToken ct)
    {
        var result = await _mocks.VerifyAsync(req.MockId, req.ExpectedVersion, ct).ConfigureAwait(false);
        await Send.OkAsync(result.Mock.ToResponse(), ct).ConfigureAwait(false);
    }
}
