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

    /// <summary>Whether any reduction (tool-result truncation or turn drop) occurred.</summary>
    public required bool Trimmed { get; init; }

    /// <summary>Number of whole messages removed by dropping oldest turns.</summary>
    public required int MessagesDropped { get; init; }

    /// <summary>Number of historical tool results shortened to an excerpt.</summary>
    public required int ToolResultsTruncated { get; init; }

    /// <summary>Total characters omitted across all truncated tool results.</summary>
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
            CharsTruncated = 0,
            EstimatedTokensBefore = estimatedTokens,
            EstimatedTokensAfter = estimatedTokens,
            ExceedsBudget = false
        };
    }
}
