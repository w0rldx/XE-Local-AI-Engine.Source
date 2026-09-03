namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Builds the ordered <see cref="ConversationMessageDto" /> context one turn SENDS, from the conversation as it is
///     persisted plus the turn's own user message. Extracted verbatim from the chat send path so a second caller — an
///     integration execution continuing a caller-managed session — replays a conversation exactly as chat does, rather
///     than growing a second, quietly diverging assembly of the same history.
///     <para>
///         A static class with no interface and no DI registration, the shape <see cref="CompactionContextResolver" />
///         already has: it is a pure function of its arguments. The regenerate path keeps its OWN builder
///         (<c>NodeChatRegenerationService.BuildRegenerationContext</c>) because it takes a cutoff and splices
///         compaction only below it — different semantics, not a duplicate.
///     </para>
/// </summary>
internal static class ConversationContextBuilder
{
    public static IReadOnlyList<ConversationMessageDto> Build(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto userMessage,
        IReadOnlyDictionary<Guid, Guid>? selectedPath,
        ConversationMessageDto? attachmentContext,
        ConversationMessageDto? imageContext = null,
        ConversationMessageDto? knowledgeContext = null)
    {
        // Collapse variant siblings to the selected path FIRST (one variant per group, newest by default), then
        // apply the existing content/status filters. Without this every regenerated sibling would be sent as
        // context; the resolver keeps only the chosen branch.
        // Every ordering/filtering below runs in ANCHOR space (the group's earliest member sequence), never on the
        // chosen sibling's own sequence: regenerating an EARLY turn after later turns exist mints a sibling whose raw
        // sequence lands past them, which would otherwise splice that answer in at the tail and break alternation.
        // See SelectedPathResolver.CreateAnchorResolver. With no variants anchor == raw sequence, so a persisted
        // CompactionSummaryCoversToSequence written before this change stays valid.
        var anchorSequence = SelectedPathResolver.CreateAnchorResolver(conversation.Messages);
        var selected = SelectedPathResolver.Resolve(conversation.Messages, selectedPath);

        // The synthetic context messages (attachment inlining, then knowledge-base grounding, then the compaction
        // synopsis) apply to plain chat only and are prepended so the model reads their content before the recent
        // conversation history. They take the first slots and the history shifts down by their count. Attachments precede
        // knowledge so uploaded files (explicitly attached this conversation) read ahead of the retrieved knowledge
        // supplement; the compaction synopsis comes last of the three so the condensed older history sits immediately
        // before the recent verbatim turns.
        var leadingContext = new List<ConversationMessageDto>(capacity: 4);
        if (attachmentContext is not null)
        {
            leadingContext.Add(attachmentContext with
            {
                SortOrder = leadingContext.Count
            });
        }

        // Image parts ride their own synthetic User message, right after any inlined attachment text, so a vision model
        // reads the images ahead of the recent conversation history (same placement rationale as the text attachments).
        if (imageContext is not null)
        {
            leadingContext.Add(imageContext with
            {
                SortOrder = leadingContext.Count
            });
        }

        if (knowledgeContext is not null)
        {
            leadingContext.Add(knowledgeContext with
            {
                SortOrder = leadingContext.Count
            });
        }

        // Non-destructive compaction: when a synopsis covers messages up to a sequence, send it in their place and drop
        // those older messages from the verbatim history. The originals remain persisted — this only shapes what is sent,
        // and the newest turns beyond the covered sequence are always kept verbatim. The synopsis message itself is
        // minted by the shared CompactionContextResolver so the regenerate path splices an identical one.
        if (CompactionContextResolver.Resolve(conversation, leadingContext.Count) is { } compaction)
        {
            leadingContext.Add(compaction.Summary);
            selected = [.. selected.Where(message => anchorSequence(message) > compaction.CoveredSequence)];
        }

        var history = selected
                      .Where(static message => !string.IsNullOrWhiteSpace(message.Content)
                                               && string.Equals(message.Status, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal))
                      .Concat([userMessage])
                      .OrderBy(anchorSequence)
                      .Select((message, index) => new ConversationMessageDto
                      {
                          Id = message.MessageId,
                          Role = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? MessageRole.Assistant : MessageRole.User,
                          Content = message.Content,
                          Thinking = message.Reasoning,
                          ModelUsed = message.Model,
                          SortOrder = index + leadingContext.Count
                      });

        return leadingContext.Count == 0 ? history.ToList() : leadingContext.Concat(history).ToList();
    }
}
