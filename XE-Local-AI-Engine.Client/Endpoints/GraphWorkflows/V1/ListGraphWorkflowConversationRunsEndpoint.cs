namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Chat;

/// <summary>The runs bound to one conversation, newest first, with the parked input and the steerable node of a live one.</summary>
public sealed class ListGraphWorkflowConversationRunsEndpoint : Endpoint<ListGraphWorkflowConversationRunsRequest, ListGraphWorkflowConversationRunsResponse>
{
    private readonly IGraphWorkflowChatService _chat;

    public ListGraphWorkflowConversationRunsEndpoint(IGraphWorkflowChatService chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        _chat = chat;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.GraphWorkflows.ConversationRuns);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ListGraphWorkflowConversationRunsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        try
        {
            var runs = await _chat.ListBoundRunsAsync(req.ConversationId, req.Limit, ct);
            await Send.OkAsync(new ListGraphWorkflowConversationRunsResponse
            {
                Runs = [.. runs.Select(static run => run.ToResponse())]
            }, ct);
        }
        catch (GraphWorkflowValidationException exception)
        {
            GraphWorkflowValidationErrors.AddTo(this, exception.Result);
            await Send.ErrorsAsync(cancellation: ct);
        }
    }
}
