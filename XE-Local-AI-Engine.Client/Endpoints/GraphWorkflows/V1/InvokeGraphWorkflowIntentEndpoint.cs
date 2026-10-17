namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     Resolves a confirmed command to one workflow open to <c>Intent</c> starts and starts it
///     (<see cref="GraphWorkflowIntentService" />). 200 for both outcomes: <c>NotMatched</c> is an answer the command
///     page falls through to chat on, not an error.
/// </summary>
/// <remarks>
///     400 for an empty utterance or request id and a future <c>utteredAt</c>; 409 <c>GraphWorkflowIntentExpired</c>
///     for a stale one, and the run service's own 409s for a start it refuses.
/// </remarks>
public sealed class InvokeGraphWorkflowIntentEndpoint : Endpoint<InvokeGraphWorkflowIntentRequest, GraphWorkflowIntentResponse>
{
    private readonly GraphWorkflowIntentService _intents;

    public InvokeGraphWorkflowIntentEndpoint(GraphWorkflowIntentService intents)
    {
        ArgumentNullException.ThrowIfNull(intents);
        _intents = intents;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.GraphWorkflows.IntentsInvoke);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces<GraphWorkflowIntentResponse>()
                                             .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(InvokeGraphWorkflowIntentRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // Audit metadata only: the operator account's id when it is a GUID, recorded on the run as its principal.
        var principalId = Guid.TryParse(User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var subject) ? subject : (Guid?)null;

        try
        {
            var result = await _intents.ResolveAndStartAsync(new GraphWorkflowIntentRequest
            {
                Utterance = req.Utterance,
                RequestId = req.RequestId,
                UtteredAt = req.UtteredAt,
                PrincipalId = principalId
            }, ct);

            await Send.OkAsync(new GraphWorkflowIntentResponse
            {
                Status = result.Status.ToString(),
                MatchedBy = result.MatchedBy.ToString(),
                DefinitionId = result.DefinitionId,
                DefinitionName = result.DefinitionName,
                Reason = result.Reason,
                Run = result.Run?.ToResponse()
            }, ct);
        }
        catch (GraphWorkflowValidationException exception)
        {
            GraphWorkflowValidationErrors.AddTo(this, exception.Result);
            await Send.ErrorsAsync(cancellation: ct);
        }
    }
}
