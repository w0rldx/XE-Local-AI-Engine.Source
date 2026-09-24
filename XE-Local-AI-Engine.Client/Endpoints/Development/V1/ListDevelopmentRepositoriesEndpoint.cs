namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class ListDevelopmentRepositoriesEndpoint : EndpointWithoutRequest<ListDevelopmentRepositoriesResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service;

    public ListDevelopmentRepositoriesEndpoint(IDevelopmentManagementService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Repositories);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var repositories = await _service.ListRepositoriesAsync(ct);
        await Send.OkAsync(new ListDevelopmentRepositoriesResponse
        {
            Items = repositories.Select(DevelopmentContractMapper.ToResponse).ToArray()
        }, ct);
    }
}
