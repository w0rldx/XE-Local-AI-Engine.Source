namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     The run's artifacts, every version of every lineage.
/// </summary>
/// <remarks>
///     The version history IS the knowledge layer, so hiding superseded rows behind a flag would cost an endpoint to
///     get them back; the client groups by <c>lineageId</c> and reads <c>isLatest</c>, which is computed here rather
///     than re-derived there.
/// </remarks>
public sealed class ListDevWorkflowArtifactsEndpoint : Endpoint<DevWorkflowArtifactFeedRequest, ListDevWorkflowArtifactsResponse>
{
    private readonly DevWorkflowRunQueryService _runQueries;

    public ListDevWorkflowArtifactsEndpoint(DevWorkflowRunQueryService runQueries)
    {
        ArgumentNullException.ThrowIfNull(runQueries);
        _runQueries = runQueries;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.RunArtifacts);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(DevWorkflowArtifactFeedRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // The run is read first so an unknown one answers 404 rather than an empty page.
        _ = await _runQueries.GetRunAsync(req.RunId, ct);

        var artifacts = await _runQueries.ListArtifactsAsync(req.RunId, req.SinceSeq, ct);
        var items = artifacts.Select(DevWorkflowContractMapper.ToResponse).ToList();
        await Send.OkAsync(new ListDevWorkflowArtifactsResponse { Items = items, LastSequence = DevWorkflowContractMapper.HighestSequence(items.Select(static item => item.Sequence)) }, ct);
    }
}
