namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

/// <summary>
///     "Answer now": ends the reasoning block of the turn writing the message, so the model answers instead of thinking
///     on, without stopping the turn.
/// </summary>
/// <remarks>
///     204 once llama-server closed the reasoning; 404 when no turn for that message is running here; 409 when the turn
///     is not reasoning right now, its model cannot be controlled (not a local llama.cpp model with a reasoning end tag),
///     or llama-server refused. The 409 body never carries the server's own text.
/// </remarks>
public sealed class AnswerNowNodeChatMessageEndpoint : Endpoint<AnswerNowNodeChatMessageRequest>
{
    private readonly InvocationReasoningControl _reasoningControl;

    public AnswerNowNodeChatMessageEndpoint(InvocationReasoningControl reasoningControl)
    {
        ArgumentNullException.ThrowIfNull(reasoningControl);
        _reasoningControl = reasoningControl;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.LocalChat.AnswerNow);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .Produces(StatusCodes.Status204NoContent)
                                             .Produces(StatusCodes.Status404NotFound)
                                             .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(AnswerNowNodeChatMessageRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        switch (await _reasoningControl.EndReasoningAsync(req.MessageId, ct))
        {
            case ReasoningEndOutcome.Ended:
                await Send.NoContentAsync(ct);
                return;
            case ReasoningEndOutcome.NotReasoning:
                AddError("The model is not thinking right now, or this model cannot be asked to answer early.");
                await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
                return;
            case ReasoningEndOutcome.Rejected:
                AddError("The model server did not accept the request to answer now. Try again, or stop the message.");
                await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
                return;
            default:
                await Send.NotFoundAsync(ct);
                return;
        }
    }
}
