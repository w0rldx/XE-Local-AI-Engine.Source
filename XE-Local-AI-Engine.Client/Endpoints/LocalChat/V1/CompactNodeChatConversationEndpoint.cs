namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;

/// <summary>
///     POST <c>chat/conversations/{conversationId}/compact</c> — non-destructive compaction. Summarizes the conversation's
///     older turns with a node-local model into an encrypted synopsis sent in their place on later turns; the original
///     messages are never deleted. Operator-gated and honors the read-only mutation guard, like rename/pin/archive.
/// </summary>
public sealed class CompactNodeChatConversationEndpoint(
    IConversationCompactionService compactionService,
    INodeChatMutationGuard mutationGuard) : Endpoint<CompactNodeChatConversationRequest, CompactNodeChatConversationResponse>
{
    private readonly IConversationCompactionService _compactionService = compactionService ?? throw new ArgumentNullException(nameof(compactionService));
    private readonly INodeChatMutationGuard _mutationGuard = mutationGuard ?? throw new ArgumentNullException(nameof(mutationGuard));

    public override void Configure()
    {
        Post(LocalApiRoutes.LocalChat.CompactConversation);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST: the conversation id comes from the route, so the generated client sends no body — and
        // therefore no Content-Type. The default POST "Accepts" metadata only allows application/json, which
        // FastEndpoints answers with 415 when the header is absent. Overriding Accepts lets the body-less request
        // through (the id still binds from the route). Mirrors CancelPreviewRunEndpoint.
        // 409 = the read-only (Origin=Remote) rejection written by the global ConflictExceptionHandler
        // (conflictType = ReadOnlyConversation); the guard exception is never caught here.
        Description(x => x.Accepts<CompactNodeChatConversationRequest>()
                          .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(CompactNodeChatConversationRequest req, CancellationToken ct)
    {
        await _mutationGuard.EnsureMutableAsync(req.ConversationId, ct).ConfigureAwait(false);

        var result = await _compactionService.CompactAsync(req.ConversationId, req.Model, ct).ConfigureAwait(false);

        if (result.Outcome == ConversationCompactionOutcome.ConversationNotFound)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(new CompactNodeChatConversationResponse
            {
                Outcome = result.Outcome.ToString(),
                Summary = result.Summary,
                CoversToSequence = result.CoversToSequence,
                MessagesFolded = result.MessagesFolded,
                UpdatedAtUtc = result.UpdatedAtUtc,
                ModelUsed = result.ModelUsed,
                UsedFallbackModel = result.UsedFallbackModel
            },
            ct).ConfigureAwait(false);
    }
}
