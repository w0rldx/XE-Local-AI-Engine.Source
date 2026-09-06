namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using System.Text.Json;
using FastEndpoints;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     Answers one pause. Thin transport and nothing else: the endpoint holds no idempotency logic of its own — the
///     operation id goes to the runtime, which answers a replayed decision with the recorded one rather than deciding
///     twice, and refuses a DIFFERENT id on an answered pause with the decision that stands on the body.
/// </summary>
public sealed class DecideGraphWorkflowNodeRunEndpoint(IGraphWorkflowRunService runs) : Endpoint<DecideGraphWorkflowNodeRunRequest, GraphWorkflowDecisionResultResponse>
{
    private readonly IGraphWorkflowRunService _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    public override void Configure()
    {
        Post(LocalApiRoutes.GraphWorkflows.RunNodeDecide);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .Produces(StatusCodes.Status404NotFound)
                                             .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(DecideGraphWorkflowNodeRunRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // Safe to parse rather than TryParse: the validator has already refused anything that is not a member name.
        // Whether THIS pause can take the one named is the runtime's answer, and it is a conflict rather than a 400.
        var decision = Enum.Parse<GraphWorkflowDecisionKind>(req.Decision, ignoreCase: true);

        // Which ACCOUNT answered. Without it the audit can say a pause was approved but not by whom, which is the one
        // question a review of an AI-driven run actually gets asked.
        var subject = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        // Re-serialized rather than passed as the raw body slice: what the answer stores has to be the value of the
        // `payload` member, not the request that carried it.
        var payload = req.Payload is { } document ? JsonSerializer.Serialize(document) : null;

        try
        {
            var result = await _runs.DecideAsync(req.RunId, req.NodeKey, req.OperationId, decision, req.Comment, payload, subject, ct).ConfigureAwait(false);
            await Send.OkAsync(new GraphWorkflowDecisionResultResponse(result.Decision.ToString(), result.RunStatus.ToString(), result.NodeRunStatus.ToString()), ct)
                      .ConfigureAwait(false);
        }
        catch (GraphWorkflowValidationException exception)
        {
            // Replayed here rather than through the global single-message handler, for the same reason the write
            // routes do it: the runtime's refusals are a keyed list, and collapsing them loses which one failed.
            GraphWorkflowValidationErrors.AddTo(this, exception.Result);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
