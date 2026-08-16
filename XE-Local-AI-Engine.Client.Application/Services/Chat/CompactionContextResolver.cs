namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Resolves a conversation's non-destructive compaction synopsis into the ONE synthetic context message that
///     replaces the covered history, shared by every path that assembles a turn's context (the send path's
///     <c>NodeChatStreamService.BuildConversationContext</c> and the regenerate path's
///     <c>NodeChatRegenerationService.BuildRegenerationContext</c>). Both must splice identically: a compacted
///     conversation that regenerates would otherwise re-send the verbatim messages the synopsis already replaced.
///     <para>
///         The originals stay persisted — this only shapes what is SENT. Callers prepend
///         <c>Summary</c> to their leading context and drop every message whose sequence is at or below
///         <c>CoveredSequence</c> from the verbatim history.
///     </para>
/// </summary>
internal static class CompactionContextResolver
{
    /// <summary>
    ///     Returns the synthetic summary message plus the sequence it covers, or <c>null</c> when the conversation
    ///     carries no synopsis (nothing to splice — the caller's context is unchanged).
    /// </summary>
    /// <param name="conversation">The conversation whose synopsis is being applied.</param>
    /// <param name="sortOrder">Slot the summary takes in the caller's leading context block.</param>
    public static (ConversationMessageDto Summary, int CoveredSequence)? Resolve(NodeChatConversationDto conversation,
        int sortOrder)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        if (conversation.CompactionSummary is not { Length: > 0 } summary || conversation.CompactionSummaryCoversToSequence is not { } coveredSequence)
        {
            return null;
        }

        return (new ConversationMessageDto
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = $"[Summary of the earlier conversation, condensed to fit the context window]\n{summary}",
            SortOrder = sortOrder
        }, coveredSequence);
    }
}
