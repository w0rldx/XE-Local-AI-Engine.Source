namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Common;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     The run's event log, paged from an exclusive watermark — the one feed that grows without bound.
/// </summary>
/// <remarks>
///     Its sequences are strictly increasing but NOT contiguous, because the run's counter is shared with node runs
///     and artifacts.
/// </remarks>
public sealed class ListDevWorkflowRunEventsEndpoint : Endpoint<DevWorkflowRunEventFeedRequest, ListDevWorkflowRunEventsResponse>
{
    private readonly DevWorkflowRunQueryService _runQueries;

    public ListDevWorkflowRunEventsEndpoint(DevWorkflowRunQueryService runQueries)
    {
        ArgumentNullException.ThrowIfNull(runQueries);
        _runQueries = runQueries;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.RunEvents);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(DevWorkflowRunEventFeedRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // The run is read first so an unknown one answers 404 rather than an empty page — a feed that pretends a
        // missing run is a quiet one is the shape a client cannot tell apart from "nothing happened yet".
        _ = await _runQueries.GetRunAsync(req.RunId, ct);

        var (events, hasMore) = await ReplayWindow.ReadAsync(req.Limit, limit => _runQueries.ListEventsAsync(req.RunId, req.SinceSeq, limit, ct));
        var page = events.Select(DevWorkflowContractMapper.ToResponse).ToList();
        await Send.OkAsync(new ListDevWorkflowRunEventsResponse
            {
                Items = page,
                LastSequence = DevWorkflowContractMapper.HighestSequence(page.Select(static item => item.Sequence)),
                HasMore = hasMore
            },
            ct);
    }
}
