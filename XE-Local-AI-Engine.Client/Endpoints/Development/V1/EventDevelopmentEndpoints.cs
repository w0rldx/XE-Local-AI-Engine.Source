namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class ListDevelopmentEventsEndpoint(IDevelopmentManagementService service)
    : Endpoint<DevelopmentProjectRequest, ListDevelopmentEventsResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Events);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentProjectRequest req, CancellationToken ct)
    {
        try
        {
            var events = await _service.ListEventsAsync(req.ProjectId, ct).ConfigureAwait(false);
            await Send.OkAsync(new ListDevelopmentEventsResponse(events.Select(DevelopmentContractMapper.ToResponse).ToArray()), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}
