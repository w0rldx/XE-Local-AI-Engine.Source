namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     The run's event log, paged from an exclusive watermark — the one feed that grows without bound, so the one that
///     pages. Its sequences are strictly increasing but NOT contiguous: the run's counter is shared with its node runs.
/// </summary>
public sealed class ListGraphWorkflowRunEventsEndpoint(IGraphWorkflowRunService runs)
    : Endpoint<GraphWorkflowRunEventFeedRequest, ListGraphWorkflowRunEventsResponse>
{
    private readonly IGraphWorkflowRunService _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    public override void Configure()
    {
        Get(LocalApiRoutes.GraphWorkflows.RunEvents);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(GraphWorkflowRunEventFeedRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var page = await _runs.ListEventsAsync(req.RunId, req.AfterSeq, ct).ConfigureAwait(false);
        await Send.OkAsync(new ListGraphWorkflowRunEventsResponse([.. page.Events.Select(GraphWorkflowContractMapper.ToResponse)],
                page.LastSeq,
                page.ReplayTruncated),
            ct).ConfigureAwait(false);
    }
}
