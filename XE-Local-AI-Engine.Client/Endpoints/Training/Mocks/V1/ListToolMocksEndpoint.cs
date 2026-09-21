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
