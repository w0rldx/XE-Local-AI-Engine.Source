namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Composes the SYNTHETIC context messages a chat turn prepends to its history: inlined attachment text, the
///     agent-mode pointer naming staged paths, a vision turn's image parts and the knowledge grounding block.
/// </summary>
/// <remarks>
///     The send and regenerate paths share it, so both produce byte-identical context for the same inputs. It does
///     NOT decide whether context is allowed: the cloud-egress gate, the agent-mode split and the vision-capability
///     check all stay with the caller, which calls only the builders its turn is entitled to.
/// </remarks>
public interface IChatTurnContextBuilder
{
    /// <summary>
    ///     Whether this turn has attachment content a withhold notice would be about, so a plain cloud chat with no
    ///     attachments stays silent: true as soon as the send names a file id, else whether one extracted text or an image.
    /// </summary>
    Task<bool> HasAttachmentContentAsync(Guid conversationId, IReadOnlyList<Guid>? requestedFileIds, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The synthetic plain-chat message inlining the extracted text of the attachments the send names, capped to
    ///     the configured budget with a truncation notice and fenced as untrusted content.
    /// </summary>
    /// <remarks>Null when there is nothing to inline; the no-attachment path short-circuits before any store call.</remarks>
    Task<ConversationMessageDto?> BuildAttachmentContextAsync(Guid conversationId, IReadOnlyList<Guid>? attachmentFileIds, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The requested image attachments as an image-only User message for a vision turn, or <see langword="null" />
    ///     when the turn attaches none.
    /// </summary>
    /// <remarks>
    ///     It is bounded by the configured per-turn image count and aggregate byte budget, because the client re-sends
    ///     every conversation attachment each turn and decrypting them all unbounded would exhaust the node. Images
    ///     beyond either cap are dropped, first-requested kept, with a warning.
    /// </remarks>
    Task<ConversationMessageDto?> BuildImageContextAsync(Guid conversationId, IReadOnlyList<Guid>? attachmentFileIds, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The top-k fused knowledge-base hits for <paramref name="query" />, composed into ONE fenced untrusted
    ///     context message alongside the provenance of the inlined hits.
    /// </summary>
    /// <param name="isRegeneratedTurn">Picks which retrieval-failure warning is logged, nothing more.</param>
    /// <remarks>
    ///     It returns <see langword="null" /> whenever grounding produces nothing — a blank or oversized query, no
    ///     matching chunks, an empty compose, or ANY retrieval failure — because grounding is a best-effort supplement
    ///     that must never fail the turn. The caller applies the cloud-egress locality gate before calling.
    /// </remarks>
    Task<KnowledgeChatGrounding?> BuildKnowledgeContextAsync(string query, bool isRegeneratedTurn = false, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The agent-mode pointer message naming the staged attachment paths, so a weak model reads the exact file
    ///     through its tools rather than guessing a name.
    /// </summary>
    /// <remarks>
    ///     File CONTENT is never inlined; only the pointer travels in context. Null when nothing was staged, leaving
    ///     the turn byte-identical to the no-attachment agent path.
    /// </remarks>
    ConversationMessageDto? BuildAgentAttachmentHint(Guid conversationId, IReadOnlyList<string> stagedAttachmentPaths);
}

/// <summary>
///     The composed knowledge-base grounding for one turn: the synthetic context message prepended to the conversation,
///     and the provenance of the inlined hits threaded to the terminal row as the turn's sources.
/// </summary>
public sealed class KnowledgeChatGrounding
{
    public required ConversationMessageDto Message { get; init; }

    public required IReadOnlyList<NodeChatMessageSource> Sources { get; init; }
}
