namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

/// <summary>
///     Deletes a session by purging its owned conversation.
///     <para>
///         <b>The purge is the whole delete.</b> The conversation footprint purge takes the session row, its executions
///         and their events with it, because those rows carry conversation-derived content and that purge is the node's
///         privacy single source of truth. What survives is the content-free kind-3 audit row per terminalized
///         execution: its <c>ConversationId</c> is null, so the purge never reaches it.
///     </para>
///     <para>
///         Refuses with 409 while an execution on the session is still <c>Accepted</c>, <c>Queued</c> or
///         <c>Running</c> — deleting then would purge the conversation out from under a live run about to persist its
///         answer into it.
///     </para>
/// </summary>
public sealed class DeleteIntegrationSessionEndpoint(IntegrationSessionService sessions) : EndpointWithoutRequest
{
    private readonly IntegrationSessionService _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Integrations.SessionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var outcome = await _sessions.DeleteAsync(Route<Guid>("sessionId"), ct).ConfigureAwait(false);
        switch (outcome)
        {
            case IntegrationSessionDeleteOutcome.Deleted:
                await Send.NoContentAsync(ct).ConfigureAwait(false);
                return;
            case IntegrationSessionDeleteOutcome.Busy:
                AddError(IntegrationSessionService.BusyMessage);
                await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct).ConfigureAwait(false);
                return;
            default:
                await Send.NotFoundAsync(ct).ConfigureAwait(false);
                return;
        }
    }
}
