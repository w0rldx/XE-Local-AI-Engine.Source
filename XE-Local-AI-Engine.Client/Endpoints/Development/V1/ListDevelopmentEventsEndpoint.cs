namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class ListDevelopmentEventsEndpoint : Endpoint<DevelopmentProjectRequest, ListDevelopmentEventsResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service;

    public ListDevelopmentEventsEndpoint(IDevelopmentManagementService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Events);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentProjectRequest req, CancellationToken ct)
    {
        var events = await _service.ListEventsAsync(req.ProjectId, ct);
        await Send.OkAsync(new ListDevelopmentEventsResponse(events.Select(DevelopmentContractMapper.ToResponse).ToArray()), ct);
    }
}
