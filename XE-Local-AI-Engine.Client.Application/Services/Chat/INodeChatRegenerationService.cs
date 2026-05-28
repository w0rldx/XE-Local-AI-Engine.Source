namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Regenerates an assistant turn (Phase 5.2) as a SIBLING VARIANT, driving the run through the SAME shared
///     runner/pump as a normal local turn. Symmetric with <see cref="INodeChatStreamService" />: one call mints the
///     linked variant placeholder (reusing <see cref="INodeChatPersistenceService.CreateMessageVariantAsync" />),
///     then drives + streams it — assistant-queued/streaming/delta/completed — over the local hub.
/// </summary>
/// <remarks>
///     The endpoint/dispatcher stays thin: this service owns only the orchestration (build context up to the parent
///     user turn, CreatePlain, run, pump-persist INTO the variant row). It never overwrites the original — the
///     regenerate is always a new sibling in the same variant_group. Origin=Remote conversations are view-only and
///     rejected by the shared mutation guard before any persistence.
/// </remarks>
public interface INodeChatRegenerationService
{
    /// <summary>
    ///     Mints a sibling variant of <paramref name="originalMessageId" /> and drives it to completion, streaming the
    ///     run as <see cref="ChatStreamEvent" />s. Throws <see cref="NodeChatReadOnlyConversationException" /> for an
    ///     Origin=Remote conversation and <see cref="InvalidOperationException" /> when the conversation or original
    ///     message is not found.
    /// </summary>
    IAsyncEnumerable<ChatStreamEvent> RegenerateAsync(Guid conversationId,
        Guid originalMessageId,
        string? reasoningEffort = null,
        bool useLocalTools = false,
        IReadOnlyDictionary<Guid, Guid>? selectedPath = null,
        CancellationToken cancellationToken = default);
}
