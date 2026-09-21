namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

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
