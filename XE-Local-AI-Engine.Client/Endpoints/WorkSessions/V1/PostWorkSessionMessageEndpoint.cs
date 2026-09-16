namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     A user follow-up into the session's owned conversation. 202: the row is persisted here, but the step that reads
///     it is the supervisor's — and on a Draft session there is no step until the operator starts it.
/// </summary>
public sealed class PostWorkSessionMessageEndpoint(IWorkSessionService service)
    : Endpoint<PostWorkSessionMessageRequest, PostWorkSessionMessageResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Post(LocalApiRoutes.WorkSessions.Messages);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<PostWorkSessionMessageResponse>(StatusCodes.Status202Accepted)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(PostWorkSessionMessageRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var messageId = await _service.PostFollowUpAsync(req.SessionId, req.Text, ct).ConfigureAwait(false);
        var session = await _service.GetAsync(req.SessionId, ct).ConfigureAwait(false);
        await Send.ResultAsync(Results.Accepted(value: new PostWorkSessionMessageResponse(messageId, session.ConversationId)))
                  .ConfigureAwait(false);
    }
}
