namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Composes the SYNTHETIC context messages a chat turn prepends to its conversation history: the inlined text of
///     the conversation's uploaded attachments, the agent-mode pointer naming the staged attachment paths, the image
///     parts of a vision turn, and the knowledge-base grounding block. Shared by the send and regenerate paths so both
///     produce byte-identical context for the same inputs.
///     <para>
///         This seam does NOT decide whether context is allowed: the cloud-egress gate
///         (<c>KnowledgeBase:AllowCloudModelAccess</c>), the agent-mode/plain-chat split and the vision-capability check
///         all stay with the caller, which calls only the builders its turn is entitled to.
///     </para>
/// </summary>
public interface IChatTurnContextBuilder
{
    /// <summary>
    ///     Whether this turn has attachment content that a withhold notice would be about — <see langword="true" />
    ///     as soon as the send names any file id, otherwise whether the conversation holds a file whose extraction
    ///     produced text or an image. Keeps a plain cloud chat with no attachments silent.
    /// </summary>
    Task<bool> HasAttachmentContentAsync(Guid conversationId, IReadOnlyList<Guid>? requestedFileIds, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The synthetic plain-chat context message inlining the extracted text of the attachments named in the send,
    ///     capped to the configured character budget with a truncation notice and fenced as untrusted content. Returns
    ///     <see langword="null" /> when there is nothing to inline (the common no-attachment path short-circuits before
    ///     any store call).
    /// </summary>
    Task<ConversationMessageDto?> BuildAttachmentContextAsync(Guid conversationId, IReadOnlyList<Guid>? attachmentFileIds, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The requested image attachments as an image-only User message (blank content) for a vision turn. Bounded by
    ///     the configured per-turn image count and aggregate byte budget — the client re-sends every conversation
    ///     attachment each turn, so decrypting them all unbounded would let a large conversation exhaust the node.
    ///     Images beyond either cap are dropped (first-requested kept) with a warning; returns <see langword="null" />
    ///     when the turn attaches no images.
    /// </summary>
    Task<ConversationMessageDto?> BuildImageContextAsync(Guid conversationId, IReadOnlyList<Guid>? attachmentFileIds, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The top-k fused knowledge-base hits for <paramref name="query" />, composed into ONE fenced untrusted context
    ///     message alongside the provenance of the inlined hits. Returns <see langword="null" /> when grounding produces
    ///     nothing: a blank/oversized query, no matching chunks, an empty compose, or ANY retrieval failure — grounding
    ///     is a best-effort supplement and must never fail the turn. The caller applies the cloud-egress locality gate
    ///     before calling.
    /// </summary>
    Task<KnowledgeChatGrounding?> BuildKnowledgeContextAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The agent-mode pointer message naming the staged attachment paths, so a weak model reads the exact staged
    ///     file (whole-file, no guessed name) through its tools. The file CONTENT is never inlined — only the pointer
    ///     travels in context. Returns <see langword="null" /> when nothing was staged, leaving the turn context
    ///     byte-identical to the no-attachment agent path.
    /// </summary>
    ConversationMessageDto? BuildAgentAttachmentHint(Guid conversationId, IReadOnlyList<string> stagedAttachmentPaths);
}

/// <summary>
///     The composed knowledge-base grounding for one turn: the synthetic context message prepended to the conversation,
///     and the provenance of the inlined hits threaded to the terminal row as the turn's sources.
/// </summary>
public sealed record KnowledgeChatGrounding(ConversationMessageDto Message, IReadOnlyList<NodeChatMessageSource> Sources);
