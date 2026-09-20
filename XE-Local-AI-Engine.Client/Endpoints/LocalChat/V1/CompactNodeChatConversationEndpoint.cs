namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;

/// <summary>
///     Non-destructive compaction of one conversation. Operator-gated.
/// </summary>
/// <remarks>
///     Summarizes the conversation's older turns with a node-local model into an encrypted synopsis sent in their
///     place on later turns; the original messages are never deleted. Honors the read-only mutation guard, like
///     rename, pin and archive.
/// </remarks>
public sealed class CompactNodeChatConversationEndpoint : Endpoint<CompactNodeChatConversationRequest, CompactNodeChatConversationResponse>
{
    private readonly IConversationCompactionService _compactionService;
    private readonly INodeChatMutationGuard _mutationGuard;

    public CompactNodeChatConversationEndpoint(
        IConversationCompactionService compactionService,
        INodeChatMutationGuard mutationGuard)
    {
        ArgumentNullException.ThrowIfNull(compactionService);
        ArgumentNullException.ThrowIfNull(mutationGuard);
        _compactionService = compactionService;
        _mutationGuard = mutationGuard;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.LocalChat.CompactConversation);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST: the conversation id comes from the route, so the generated client sends no body and no Content-Type, which the default POST "Accepts" metadata
        // answers with 415; overriding Accepts lets it through. The 409 is the read-only rejection the global ConflictExceptionHandler writes with conflictType ReadOnlyConversation.
        Description(x => x.Accepts<CompactNodeChatConversationRequest>()
                          .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(CompactNodeChatConversationRequest req, CancellationToken ct)
    {
        await _mutationGuard.EnsureMutableAsync(req.ConversationId, ct);

        var result = await _compactionService.CompactAsync(req.ConversationId, req.Model, ct);

        if (result.Outcome == ConversationCompactionOutcome.ConversationNotFound)
        {
            await Send.NotFoundAsync(ct);
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
            ct);
    }
}
