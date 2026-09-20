namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;

/// <summary>
///     Builds the ordered <see cref="ConversationMessageDto" /> context one turn SENDS, from the persisted
///     conversation plus the turn's own user message.
/// </summary>
/// <remarks>
///     It was extracted verbatim from the chat send path so an integration execution continuing a caller-managed
///     session replays a conversation exactly as chat does, rather than growing a second, diverging assembly. It is a
///     static class with no interface and no DI registration, the shape <see cref="CompactionContextResolver" />
///     already has, because it is a pure function of its arguments. The regenerate path keeps its own builder: it
///     takes a cutoff and splices compaction only below it, which is different semantics, not a duplicate.
/// </remarks>
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
        // Variant siblings collapse to the selected path FIRST, or every regenerated sibling would be sent. Everything
        // below runs in ANCHOR space, never a sibling's own sequence, which would break user/assistant alternation.
        var anchorSequence = SelectedPathResolver.CreateAnchorResolver(conversation.Messages);
        var selected = SelectedPathResolver.Resolve(conversation.Messages, selectedPath);

        // The synthetic context messages are plain-chat only and take the first slots, so the history shifts down by
        // their count: attachments, then knowledge, then the synopsis, which sits nearest the verbatim turns.
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

        // The ids of turns kept below the compaction cutoff ONLY for their tool exchanges: such a turn contributes its
        // actions and nothing else, since the synopsis already carries its prose. Null while nothing is compacted.
        HashSet<Guid>? exchangeOnlySurvivors = null;

        // Non-destructive compaction: a synopsis is sent in place of the messages it covers, which only shapes what is
        // SENT. It is minted by the shared CompactionContextResolver so the regenerate path splices an identical one.
        if (CompactionContextResolver.Resolve(conversation, leadingContext.Count) is { } compaction)
        {
            leadingContext.Add(compaction.Summary);
            var kept = new List<NodeChatPersistedMessageDto>(selected.Count);
            foreach (var message in selected)
            {
                if (anchorSequence(message) > compaction.CoveredSequence)
                {
                    kept.Add(message);
                }
                else if (SurvivesCompactionForToolHistory(message, includeToolHistory))
                {
                    kept.Add(message);
                    _ = (exchangeOnlySurvivors ??= []).Add(message.MessageId);
                }
            }

            selected = kept;
        }

        var history = selected
                      .Where(message => IsSendable(message) || (includeToolHistory && HasCompletedToolPart(message)))
                      .Concat([userMessage])
                      .OrderBy(anchorSequence)
                      .Select((message, index) =>
                      {
                          var isAssistant = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase);
                          var exchangeOnly = exchangeOnlySurvivors?.Contains(message.MessageId) == true;
                          return new ConversationMessageDto
                          {
                              Id = message.MessageId,
                              Role = isAssistant ? MessageRole.Assistant : MessageRole.User,
                              Content = exchangeOnly ? string.Empty : message.Content,
                              Thinking = exchangeOnly ? null : message.Reasoning,
                              ModelUsed = message.Model,
                              SortOrder = index + leadingContext.Count,
                              ToolExchanges = includeToolHistory && isAssistant ? ProjectToolExchanges(message, toolResultExcerptChars) : null
                          };
                      });

        return leadingContext.Count == 0 ? history.ToList() : leadingContext.Concat(history).ToList();
    }

    /// <summary>
    ///     The send filter: a completed, content-bearing turn. It is its own predicate so the tool-history branch
    ///     reads as an ADDITION rather than a rewrite, and with that flag off the two are the original expression.
    /// </summary>
    private static bool IsSendable(NodeChatPersistedMessageDto message) =>
        !string.IsNullOrWhiteSpace(message.Content)
        && string.Equals(message.Status, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal);

    /// <summary>
    ///     Whether a turn at or below the compaction cutoff outlives it anyway.
    /// </summary>
    /// <remarks>
    ///     The synopsis is PROSE: it summarizes text and never records the actions a turn took, so ANY turn that
    ///     completed a tool call survives the fold for its exchanges, whatever its status. A survivor that WAS
    ///     sendable survives for its exchanges alone — <see cref="Build" /> blanks its content and reasoning, which
    ///     the synopsis already carries.
    /// </remarks>
    internal static bool SurvivesCompactionForToolHistory(NodeChatPersistedMessageDto message, bool includeToolHistory) =>
        includeToolHistory && HasCompletedToolPart(message);

    /// <summary>
    ///     Whether an ASSISTANT turn carries at least one completed tool part, which keeps it even when it failed or
    ///     its text is blank: a run that called a tool and then died left a real side effect.
    /// </summary>
    private static bool HasCompletedToolPart(NodeChatPersistedMessageDto message) =>
        string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
        && message.Parts is { Count: > 0 } parts
        && parts.Any(IsCompletedToolPart);

    /// <summary>
    ///     The exchange list <see cref="Build" /> would attach to this turn with tool history on, or null when it
    ///     carries none. Internal, so the step bound measures exactly what the send path will carry.
    /// </summary>
    internal static IReadOnlyList<ConversationToolExchange>? ProjectSendableToolExchanges(NodeChatPersistedMessageDto message, int toolResultExcerptChars) =>
        string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
            ? ProjectToolExchanges(message, toolResultExcerptChars)
            : null;

    /// <summary>
    ///     Projects an assistant turn's persisted tool parts into replayable exchanges, ordered by the stamped part
    ///     sequence.
    /// </summary>
    /// <remarks>
    ///     A requested-but-never-completed part is skipped, since an orphan call with no result is worse than no call
    ///     at all. Each result is capped at projection time so one huge historical result cannot ride every later
    ///     continuation, with the same marker the context budgeter uses, so a twice-truncated result still reads as one.
    /// </remarks>
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

            (exchanges ??= []).Add(new ConversationToolExchange
            {
                CallId = part.ToolCallId!,
                Name = part.Name ?? string.Empty,
                ArgumentsJson = part.Args,
                Result = ExcerptResult(part.Result, toolResultExcerptChars),
                IsError = string.Equals(part.State, NodeChatToolPartStates.Failed, StringComparison.Ordinal)
            });
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
