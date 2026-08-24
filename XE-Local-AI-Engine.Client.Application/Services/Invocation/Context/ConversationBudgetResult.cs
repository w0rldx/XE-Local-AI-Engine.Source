namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

using Microsoft.Extensions.AI;

/// <summary>
///     Outcome of a single <see cref="IConversationContextBudgeter.Budget" /> pass: the (possibly reduced) message list
///     to send plus sanitized counters describing what was trimmed. Carries no message content so it is safe to log.
/// </summary>
public sealed record ConversationBudgetResult
{
    /// <summary>The budgeted message list. Reference-equal to the input when nothing was trimmed (the passthrough fast path).</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>
    ///     Whether any reduction occurred — a tool-result truncation, a turn/approval-group drop, a Pass 4 reasoning
    ///     strip, or a Pass 5 protected tool-result excerpt.
    /// </summary>
    public required bool Trimmed { get; init; }

    /// <summary>
    ///     Number of whole messages removed: the oldest dropped turns and evicted approval groups, plus any message Pass 4
    ///     emptied by stripping its reasoning (a reasoning-only message is dropped whole rather than sent empty).
    /// </summary>
    public required int MessagesDropped { get; init; }

    /// <summary>Number of HISTORICAL tool results shortened to an excerpt by Pass 1 (outside the protected recent window).</summary>
    public required int ToolResultsTruncated { get; init; }

    /// <summary>
    ///     Number of surviving messages Pass 4 removed <see cref="TextReasoningContent" /> from — including any that were
    ///     dropped because reasoning was all they carried. Zero whenever
    ///     <see cref="ConversationContextBudgetOptions.StripProtectedReasoning" /> is off or the ordinary passes already
    ///     met the budget.
    /// </summary>
    public required int ReasoningStrippedCount { get; init; }

    /// <summary>
    ///     Number of tool results inside the PROTECTED recent window that Pass 5 shortened to an excerpt. Zero whenever
    ///     <see cref="ConversationContextBudgetOptions.ExcerptProtectedToolResults" /> is off or the earlier passes already
    ///     met the budget.
    /// </summary>
    public required int ProtectedResultsExcerptedCount { get; init; }

    /// <summary>Total characters omitted across every truncated tool result, Pass 1 and Pass 5 combined.</summary>
    public required int CharsTruncated { get; init; }

    /// <summary>Estimated total tokens of the input list before budgeting.</summary>
    public required int EstimatedTokensBefore { get; init; }

    /// <summary>Estimated total tokens of the budgeted list.</summary>
    public required int EstimatedTokensAfter { get; init; }

    /// <summary>
    ///     True when the always-keep set (system messages plus the most recent turns) still exceeds the budget after all
    ///     reductions. The set is kept anyway (per-message size is bounded elsewhere); this flags the overrun for logging.
    /// </summary>
    public required bool ExceedsBudget { get; init; }

    /// <summary>A no-op result that returns the input unchanged (used on the under-budget fast path).</summary>
    public static ConversationBudgetResult Unchanged(IReadOnlyList<ChatMessage> messages, int estimatedTokens)
    {
        return new ConversationBudgetResult
        {
            Messages = messages,
            Trimmed = false,
            MessagesDropped = 0,
            ToolResultsTruncated = 0,
            ReasoningStrippedCount = 0,
            ProtectedResultsExcerptedCount = 0,
            CharsTruncated = 0,
            EstimatedTokensBefore = estimatedTokens,
            EstimatedTokensAfter = estimatedTokens,
            ExceedsBudget = false
        };
    }
}
