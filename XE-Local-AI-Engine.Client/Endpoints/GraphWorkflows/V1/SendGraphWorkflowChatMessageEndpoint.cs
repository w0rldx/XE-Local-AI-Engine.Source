namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Chat;

/// <summary>
///     A chat message into workflow mode: starts a run bound to the conversation, or answers the run's parked ChatInput.
///     202 like a start — the dispatcher advances the run out of band.
/// </summary>
/// <remarks>
///     The three chat conflicts reach the client through <c>ConflictExceptionHandler</c>: <c>GraphWorkflowRunBusy</c>,
///     <c>GraphWorkflowRerunConfirmationRequired</c> and <c>GraphWorkflowAttachmentsNotAccepted</c>.
/// </remarks>
public sealed class SendGraphWorkflowChatMessageEndpoint : Endpoint<SendGraphWorkflowChatMessageRequest, SendGraphWorkflowChatMessageResponse>
{
    private readonly IGraphWorkflowChatService _chat;

    public SendGraphWorkflowChatMessageEndpoint(IGraphWorkflowChatService chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        _chat = chat;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.GraphWorkflows.ConversationMessages);
        Policies(NodeAuthorizationPolicies.Operator);
        Options(static builder => builder.WithMetadata(new GraphWorkflowRequestSizeLimit()));
        Description(static builder => builder.Produces<SendGraphWorkflowChatMessageResponse>(StatusCodes.Status202Accepted)
                                             .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .Produces(StatusCodes.Status404NotFound)
                                             .ProducesConflictProblemDetails()
                                             .ProducesProblem(StatusCodes.Status413PayloadTooLarge));
    }

    public override async Task HandleAsync(SendGraphWorkflowChatMessageRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (GraphWorkflowRequestSizeLimit.IsOversized(HttpContext.Request))
        {
            await Send.ResultAsync(RequestBodyTooLargeProblem.Result(GraphWorkflowRequestSizeLimit.OversizedDetail));
            return;
        }

        try
        {
            var result = await _chat.SendAsync(req.ConversationId,
                new GraphWorkflowChatSendRequest
                {
                    RequestId = req.RequestId,
                    DefinitionId = req.DefinitionId,
                    Content = req.Content,
                    AttachmentFileIds = req.AttachmentFileIds ?? [],
                    ConfirmRerun = req.ConfirmRerun ?? false
                },
                User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value,
                ct);
            var action = result.Action == GraphWorkflowChatSendAction.Started ? "started" : "answered";
            await Send.ResultAsync(Results.Accepted(value: new SendGraphWorkflowChatMessageResponse
            {
                RunId = result.RunId,
                MessageId = result.MessageId,
                Action = action
            }));
        }
        catch (GraphWorkflowValidationException exception)
        {
            GraphWorkflowValidationErrors.AddTo(this, exception.Result);
            await Send.ErrorsAsync(cancellation: ct);
        }
    }
}
