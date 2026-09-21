namespace XE_Local_AI_Engine.Client.Endpoints.AgentHome.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.AgentHome.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     One run's event log as text, so an operator can read what the agent did rather than infer it from an outcome
///     badge.
/// </summary>
/// <remarks>
///     A run the node will not serve is a 404, under the same gates as the delete. A run that exists with no readable
///     log is a 200 carrying empty text: the question was answered, and "this run left nothing here" is not the same
///     answer as "no such run".
/// </remarks>
public sealed class GetAgentHomeRunLogEndpoint : Endpoint<AgentHomeRunByIdRequest, AgentHomeRunTextResponse>
{
    private readonly IAgentHomeRunListService _service;

    public GetAgentHomeRunLogEndpoint(IAgentHomeRunListService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.AgentHomeRuns.Log);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces<AgentHomeRunTextResponse>(StatusCodes.Status200OK)
                                             .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(AgentHomeRunByIdRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var text = await _service.ReadLogAsync(req.RunId, ct);
        if (text is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(text.ToResponse(), ct);
    }
}
