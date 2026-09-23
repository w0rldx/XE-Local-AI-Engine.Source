namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Chat;

/// <summary>
///     Steers the running Agent or LLM call node of a chat-bound run: 202 with the run, because the dispatcher applies
///     the steer on its next tick. Refusals: 400 for the request, 404 for an unknown run or node, 409 for the run's state.
/// </summary>
/// <remarks>The 409s reach the client through <c>ConflictExceptionHandler</c>: <c>GraphWorkflowRunConflict</c> and <c>GraphWorkflowSteerLimitReached</c>.</remarks>
public sealed class SteerGraphWorkflowNodeRunEndpoint : Endpoint<SteerGraphWorkflowNodeRunRequest, GraphWorkflowRunResponse>
{
    private readonly IGraphWorkflowChatService _chat;

    public SteerGraphWorkflowNodeRunEndpoint(IGraphWorkflowChatService chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        _chat = chat;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.GraphWorkflows.RunNodeSteer);
        Policies(NodeAuthorizationPolicies.Operator);
        Options(static builder => builder.WithMetadata(new GraphWorkflowRequestSizeLimit()));
        Description(static builder => builder.Produces<GraphWorkflowRunResponse>(StatusCodes.Status202Accepted)
                                             .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .Produces(StatusCodes.Status404NotFound)
                                             .ProducesConflictProblemDetails()
                                             .ProducesProblem(StatusCodes.Status413PayloadTooLarge));
    }

    public override async Task HandleAsync(SteerGraphWorkflowNodeRunRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (GraphWorkflowRequestSizeLimit.IsOversized(HttpContext.Request))
        {
            await Send.ResultAsync(RequestBodyTooLargeProblem.Result(GraphWorkflowRequestSizeLimit.OversizedDetail));
            return;
        }

        try
        {
            var detail = await _chat.SteerAsync(req.RunId,
                req.NodeKey,
                new GraphWorkflowChatSteerRequest { OperationId = req.OperationId, Message = req.Message },
                User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value,
                ct);
            await Send.ResultAsync(Results.Accepted(value: detail.ToResponse()));
        }
        catch (GraphWorkflowValidationException exception)
        {
            GraphWorkflowValidationErrors.AddTo(this, exception.Result);
            await Send.ErrorsAsync(cancellation: ct);
        }
    }
}
