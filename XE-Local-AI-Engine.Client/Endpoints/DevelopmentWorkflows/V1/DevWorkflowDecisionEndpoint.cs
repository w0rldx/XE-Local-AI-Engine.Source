namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     The ONE decision surface: a gate's answer and a stuck node run's intervention are the same human act, so
///     <c>Approve</c>, <c>Reject</c>, <c>RequestChanges</c>, <c>Retry</c>, <c>Skip</c> and <c>Abandon</c> all travel
///     this route into one table, one audit shape and one idempotency path.
///     <para>
///         Thin transport and nothing else. The endpoint holds no idempotency logic of its own: the operation id goes
///         to the runtime, which answers a replayed decision with the recorded one rather than deciding twice. A
///         DIFFERENT operation id at an already-answered node run is not a replay but a second human act, and it is
///         refused with the standing decision on the body.
///     </para>
/// </summary>
public sealed class DecideDevWorkflowNodeRunEndpoint(IDevWorkflowRunService runs) : Endpoint<DevWorkflowDecisionRequest, DevWorkflowDecisionResultResponse>
{
    private readonly IDevWorkflowRunService _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    public override void Configure()
    {
        Post(LocalApiRoutes.DevelopmentWorkflows.NodeRunDecision);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(DevWorkflowDecisionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // Safe to parse rather than TryParse: the validator has already refused anything that is not a member. Whether
        // THIS node run can take the one named is the runtime's answer, and it is a conflict rather than a bad request.
        var decision = Enum.Parse<DevWorkflowDecisionKind>(req.Decision, ignoreCase: true);

        // Which ACCOUNT decided. Without it the audit can say a gate was approved but not by whom, which is the one
        // question a review of an AI-driven change actually gets asked.
        var subject = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        var result = await _runs.DecideAsync(req.RunId, req.NodeRunId, req.OperationId, decision, req.Comment, req.PayloadJson, subject, ct)
                                .ConfigureAwait(false);

        // The run status may still read Running or WaitingForApproval: what follows a decision is the dispatcher's
        // work, taken out of band on its own clock. Reporting the CURRENT state is the same honesty the 202s encode.
        var nodeRun = result.Detail.NodeRuns.FirstOrDefault(entry => entry.Id == req.NodeRunId)
                      ?? throw new DevWorkflowNotFoundException($"Node run '{req.NodeRunId}' was not found on run '{req.RunId}' after its decision.");
        await Send.OkAsync(new DevWorkflowDecisionResultResponse(result.Decision.ToResponse(), result.Detail.Run.Status.ToString(), nodeRun.Status.ToString()), ct)
                  .ConfigureAwait(false);
    }
}
