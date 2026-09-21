namespace XE_Local_AI_Engine.Client.Endpoints.AgentHome.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.AgentHome.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.AgentHome.V1.Validators;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     The node's AgentHome run history, newest first and paged server-side. Operator-gated, like every other
///     AgentHome surface.
/// </summary>
/// <remarks>
///     A run has no database row: the answer is a bounded scan of the runs directory, and each row is read from that
///     run's own capped files. A node with no runs directory answers an empty page, never a 404 — "no runs yet" is
///     the honest answer to the question this endpoint was asked.
/// </remarks>
public sealed class ListAgentHomeRunsEndpoint : Endpoint<ListAgentHomeRunsRequest, ListAgentHomeRunsResponse>
{
    /// <summary>The page size a caller that names none gets.</summary>
    private const int DefaultLimit = 25;

    private readonly IAgentHomeRunListService _service;

    public ListAgentHomeRunsEndpoint(IAgentHomeRunListService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.AgentHomeRuns.List);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<ListAgentHomeRunsResponse>(StatusCodes.Status200OK)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(ListAgentHomeRunsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var page = await _service.ListAsync(Math.Clamp(req.Limit ?? DefaultLimit, min: 1, ListAgentHomeRunsRequestValidator.MaxLimit),
            Math.Max(req.Offset ?? 0, val2: 0),
            ct);

        await Send.OkAsync(page.ToResponse(), ct);
    }
}
