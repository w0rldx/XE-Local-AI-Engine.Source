namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

public sealed class ListDevWorkflowRunsEndpoint : Endpoint<ListDevWorkflowRunsRequest, ListDevWorkflowRunsResponse>
{
    private readonly DevWorkflowRunQueryService _runQueries;

    public ListDevWorkflowRunsEndpoint(DevWorkflowRunQueryService runQueries)
    {
        ArgumentNullException.ThrowIfNull(runQueries);
        _runQueries = runQueries;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.Runs);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(ListDevWorkflowRunsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // Safe to parse rather than TryParse: the validator has already refused anything that is not a member.
        var status = req.Status is null ? (DevWorkflowRunStatus?)null : Enum.Parse<DevWorkflowRunStatus>(req.Status, ignoreCase: true);
        var runs = await _runQueries.ListRunSummariesAsync(req.WorkItemId, status, req.Limit, ct);
        await Send.OkAsync(new ListDevWorkflowRunsResponse { Items = [.. runs.Select(DevWorkflowContractMapper.ToResponse)] }, ct);
    }
}

/// <summary>
///     Starts a run of one definition for one work item. 202, not 200: the endpoint commits a durable intent and the
///     dispatcher advances it out of band, so the body legitimately reads <c>Pending</c>.
///     <para>
///         The two refusals both come from the runtime, which holds the pinned graph: a graph with repo-bound nodes on
///         a work item that names no project is a 400, and a work item that already has a live run is a 409.
///     </para>
/// </summary>
public sealed class StartDevWorkflowRunEndpoint : Endpoint<StartDevWorkflowRunRequest, DevWorkflowRunResponse>
{
    private readonly DevWorkflowRunComposer _composer;
    private readonly IDevWorkflowRunService _runs;

    public StartDevWorkflowRunEndpoint(IDevWorkflowRunService runs, DevWorkflowRunComposer composer)
    {
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentNullException.ThrowIfNull(runs);
        _composer = composer;
        _runs = runs;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.DevelopmentWorkflows.WorkItemRuns);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<DevWorkflowRunResponse>(StatusCodes.Status202Accepted)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(StartDevWorkflowRunRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var detail = await _runs.StartAsync(req.WorkItemId, req.DefinitionId, req.InputsJson, req.OperationId, ct);
        await Send.ResultAsync(Results.Accepted(value: await _composer.ComposeAsync(detail, ct)));
    }
}

public sealed class GetDevWorkflowRunEndpoint : Endpoint<DevWorkflowRunRequest, DevWorkflowRunResponse>
{
    private readonly DevWorkflowRunComposer _composer;
    private readonly IDevWorkflowRunService _runs;

    public GetDevWorkflowRunEndpoint(IDevWorkflowRunService runs, DevWorkflowRunComposer composer)
    {
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentNullException.ThrowIfNull(runs);
        _composer = composer;
        _runs = runs;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.RunById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(DevWorkflowRunRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var detail = await _runs.GetAsync(req.RunId, ct);
        await Send.OkAsync(await _composer.ComposeAsync(detail, ct), ct);
    }
}

/// <summary>
///     Asks the run to pause. 202 like the other three: the body it returns reads <c>Pausing</c>, because live node
///     runs drain first — a 200 would tell a schema-trusting client the pause had finished.
/// </summary>
public sealed class PauseDevWorkflowRunEndpoint : Endpoint<DevWorkflowRunActionRequest, DevWorkflowRunResponse>
{
    private readonly DevWorkflowRunComposer _composer;
    private readonly IDevWorkflowRunService _runs;

    public PauseDevWorkflowRunEndpoint(IDevWorkflowRunService runs, DevWorkflowRunComposer composer)
    {
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentNullException.ThrowIfNull(runs);
        _composer = composer;
        _runs = runs;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.DevelopmentWorkflows.RunPause);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<DevWorkflowRunResponse>(StatusCodes.Status202Accepted)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(DevWorkflowRunActionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var detail = await _runs.PauseAsync(req.RunId, req.OperationId, ct);
        await Send.ResultAsync(Results.Accepted(value: await _composer.ComposeAsync(detail, ct)));
    }
}

public sealed class ResumeDevWorkflowRunEndpoint : Endpoint<DevWorkflowRunActionRequest, DevWorkflowRunResponse>
{
    private readonly DevWorkflowRunComposer _composer;
    private readonly IDevWorkflowRunService _runs;

    public ResumeDevWorkflowRunEndpoint(IDevWorkflowRunService runs, DevWorkflowRunComposer composer)
    {
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentNullException.ThrowIfNull(runs);
        _composer = composer;
        _runs = runs;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.DevelopmentWorkflows.RunResume);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<DevWorkflowRunResponse>(StatusCodes.Status202Accepted)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(DevWorkflowRunActionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var detail = await _runs.ResumeAsync(req.RunId, req.OperationId, ct);
        await Send.ResultAsync(Results.Accepted(value: await _composer.ComposeAsync(detail, ct)));
    }
}

public sealed class CancelDevWorkflowRunEndpoint : Endpoint<DevWorkflowRunActionRequest, DevWorkflowRunResponse>
{
    private readonly DevWorkflowRunComposer _composer;
    private readonly IDevWorkflowRunService _runs;

    public CancelDevWorkflowRunEndpoint(IDevWorkflowRunService runs, DevWorkflowRunComposer composer)
    {
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentNullException.ThrowIfNull(runs);
        _composer = composer;
        _runs = runs;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.DevelopmentWorkflows.RunCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<DevWorkflowRunResponse>(StatusCodes.Status202Accepted)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(DevWorkflowRunActionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var detail = await _runs.CancelAsync(req.RunId, req.OperationId, ct);
        await Send.ResultAsync(Results.Accepted(value: await _composer.ComposeAsync(detail, ct)));
    }
}

/// <summary>
///     The run's event log, paged from an exclusive watermark. The one feed that grows without bound, so the one that
///     pages; its sequences are strictly increasing but NOT contiguous, because the run's counter is shared with node
///     runs and artifacts.
/// </summary>
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

        // One over the limit, so "there is more" is observed rather than inferred from a full page.
        var events = await _runQueries.ListEventsAsync(req.RunId, req.SinceSeq, req.Limit + 1, ct);
        var page = events.Take(req.Limit).Select(DevWorkflowContractMapper.ToResponse).ToList();
        await Send.OkAsync(new ListDevWorkflowRunEventsResponse
        {
            Items = page,
            LastSequence = DevWorkflowContractMapper.HighestSequence(page.Select(static item => item.Sequence)),
            HasMore = events.Count > req.Limit
        },
            ct);
    }
}
