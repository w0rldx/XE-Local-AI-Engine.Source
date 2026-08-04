namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.AI.Contracts.Events;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Loopback responder for a pending <c>ask_user</c> question — the question analogue of
///     <see cref="ResolveToolApprovalEndpoint" />. The parked turn is waiting on the operator, and in desktop/local mode
///     there is no worker hub to carry the answers, so the browser posts them here; the handler feeds them into
///     <see cref="IWorkerEventDispatcher.DispatchUserQuestionAnsweredAsync" />, which releases the waiting run. Keyed
///     only by the question request id (the runner's opaque per-question key), so it works with no platform connection
///     and needs no conversation context.
/// </summary>
public sealed class ResolveUserQuestionEndpoint(IWorkerEventDispatcher eventDispatcher)
    : Endpoint<ResolveUserQuestionRequest, ResolveUserQuestionResponse>
{
    private readonly IWorkerEventDispatcher _eventDispatcher = eventDispatcher ?? throw new ArgumentNullException(nameof(eventDispatcher));

    public override void Configure()
    {
        Post(LocalApiRoutes.LocalChat.ResolveUserQuestion);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ResolveUserQuestionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // DispatchUserQuestionAnsweredAsync is idempotent/safe when no question is pending for this id (it logs and
        // no-ops), so a duplicate submit or an answer posted after the turn already moved on never faults the run.
        var answers = req.Answers
                         .Select(static answer => new UserQuestionAnswer(answer.Question,
                             answer.Selected is null ? [] : [.. answer.Selected],
                             answer.Other))
                         .ToArray();

        await _eventDispatcher.DispatchUserQuestionAnsweredAsync(new UserQuestionAnsweredEvent(req.RequestId, answers)).ConfigureAwait(false);

        // The answers are the operator's words: the response echoes the correlation id and a count only, never content.
        await Send.OkAsync(new ResolveUserQuestionResponse
        {
            RequestId = req.RequestId,
            AnswerCount = answers.Length
        }, ct).ConfigureAwait(false);
    }
}
