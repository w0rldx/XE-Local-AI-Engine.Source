namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class ListDevelopmentProjectsEndpoint : EndpointWithoutRequest<ListDevelopmentProjectsResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service;

    public ListDevelopmentProjectsEndpoint(IDevelopmentManagementService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Projects);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var projects = await _service.ListProjectsAsync(ct);
        await Send.OkAsync(new ListDevelopmentProjectsResponse { Items = projects.Select(DevelopmentContractMapper.ToResponse).ToArray() }, ct);
    }
}
