namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;

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
        ConversationMessageDto? knowledgeContext = null,
        bool includeToolHistory = false,
        int toolResultExcerptChars = ConversationContextBudgetOptions.DefaultHistoricalToolResultExcerptChars)
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
                      .Where(message => IsSendable(message) || (includeToolHistory && HasCompletedToolPart(message)))
                      .Concat([userMessage])
                      .OrderBy(anchorSequence)
                      .Select((message, index) =>
                      {
                          var isAssistant = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase);
                          return new ConversationMessageDto
                          {
                              Id = message.MessageId,
                              Role = isAssistant ? MessageRole.Assistant : MessageRole.User,
                              Content = message.Content,
                              Thinking = message.Reasoning,
                              ModelUsed = message.Model,
                              SortOrder = index + leadingContext.Count,
                              ToolExchanges = includeToolHistory && isAssistant ? ProjectToolExchanges(message, toolResultExcerptChars) : null
                          };
                      });

        return leadingContext.Count == 0 ? history.ToList() : leadingContext.Concat(history).ToList();
    }

    /// <summary>
    ///     The unchanged send filter: a completed, content-bearing turn. Kept as its own predicate so the tool-history
    ///     branch reads as an ADDITION to it rather than a rewrite of it — with the flag off the two together are the
    ///     original expression exactly.
    /// </summary>
    private static bool IsSendable(NodeChatPersistedMessageDto message) =>
        !string.IsNullOrWhiteSpace(message.Content)
        && string.Equals(message.Status, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal);

    /// <summary>
    ///     Whether an ASSISTANT turn carries at least one completed tool part. Such a turn is kept even when it is
    ///     <c>Failed</c>/<c>Cancelled</c> or its text is blank: a run that called a tool and then died left a real side
    ///     effect, and hiding it is exactly the hole replaying tool history exists to close.
    /// </summary>
    private static bool HasCompletedToolPart(NodeChatPersistedMessageDto message) =>
        string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
        && message.Parts is { Count: > 0 } parts
        && parts.Any(IsCompletedToolPart);

    /// <summary>
    ///     The exchange list <see cref="Build" /> would attach to this persisted turn with tool history on, or null when
    ///     it carries none. Internal so the step bound's projection measures exactly what the send path will carry
    ///     rather than a second, quietly diverging idea of it.
    /// </summary>
    internal static IReadOnlyList<ConversationToolExchange>? ProjectSendableToolExchanges(NodeChatPersistedMessageDto message, int toolResultExcerptChars) =>
        string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
            ? ProjectToolExchanges(message, toolResultExcerptChars)
            : null;

    /// <summary>
    ///     Projects an assistant turn's persisted tool parts into the replayable exchanges, ordered by the part sequence
    ///     the accumulator stamped. A requested-but-never-completed part is skipped: an orphan call with no result is
    ///     worse than no call at all. Each result is capped here, at projection time, so a single huge historical result
    ///     cannot ride every later continuation unbounded — with the same marker the context budgeter uses, so one
    ///     result truncated twice does not read as two different results.
    /// </summary>
    private static IReadOnlyList<ConversationToolExchange>? ProjectToolExchanges(NodeChatPersistedMessageDto message, int toolResultExcerptChars)
    {
        if (message.Parts is not { Count: > 0 } parts)
        {
            return null;
        }

        List<ConversationToolExchange>? exchanges = null;
        foreach (var part in parts.OrderBy(static part => part.Sequence))
        {
            if (!IsCompletedToolPart(part))
            {
                continue;
            }

            (exchanges ??= []).Add(new ConversationToolExchange(part.ToolCallId!,
                part.Name ?? string.Empty,
                part.Args,
                ExcerptResult(part.Result, toolResultExcerptChars),
                string.Equals(part.State, NodeChatToolPartStates.Failed, StringComparison.Ordinal)));
        }

        return exchanges;
    }

    /// <summary>
    ///     A tool part that reached a terminal state and carries the call id the replayed pair correlates on. The
    ///     accumulator refuses an empty id, but a legacy part persisted before that guard existed can still carry one.
    /// </summary>
    private static bool IsCompletedToolPart(NodeChatMessagePart part) =>
        string.Equals(part.Kind, NodeChatMessagePartKinds.Tool, StringComparison.Ordinal)
        && !string.IsNullOrEmpty(part.ToolCallId)
        && (string.Equals(part.State, NodeChatToolPartStates.Received, StringComparison.Ordinal)
            || string.Equals(part.State, NodeChatToolPartStates.Failed, StringComparison.Ordinal));

    private static string? ExcerptResult(string? result, int toolResultExcerptChars)
    {
        var excerptChars = Math.Max(val1: 0, toolResultExcerptChars);
        return result is null || result.Length <= excerptChars
            ? result
            : ConversationContextBudgeter.Excerpt(result, excerptChars, result.Length - excerptChars);
    }
}
