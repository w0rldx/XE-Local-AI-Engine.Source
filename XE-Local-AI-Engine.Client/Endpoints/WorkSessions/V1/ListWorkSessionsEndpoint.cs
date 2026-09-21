namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

public sealed class ListWorkSessionsEndpoint : EndpointWithoutRequest<ListWorkSessionsResponse>
{
    private readonly IWorkSessionService _service;

    public ListWorkSessionsEndpoint(IWorkSessionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.Root);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var sessions = await _service.ListAsync(ct);
        await Send.OkAsync(new ListWorkSessionsResponse { Items = [.. sessions.Select(WorkSessionContractMapper.ToResponse)] }, ct);
    }
}
