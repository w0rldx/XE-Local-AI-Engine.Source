namespace XE_Local_AI_Engine.AI.Agent.Chat;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Configuration;

/// <summary>
///     Deterministic, LLM-free reducer that fits a SINGLE raw provider round's message set into the effective context
///     window, used by the provider-boundary budget middleware. Policy: always keep system messages, the most recent
///     <see cref="ProviderCallBudgetOptions.RecentMessagesToKeep" /> messages, and the very last message (the pending
///     tool result the model must see next); when the estimate exceeds the window it first excerpts oversized tool
///     results anywhere — including a recent/pending one — to a marked excerpt (so the pending result is bounded, never
///     dropped), then drops the oldest non-protected, non-system messages whole. This is the innermost analogue of the
///     application layer's turn-grouped budgeter, operating on the flat message list MAF hands the raw
///     <see cref="IChatClient" /> after appending inner tool results.
/// </summary>
internal static class ProviderCallBudgeter
{
    public static ProviderBudgetResult Budget(IReadOnlyList<ChatMessage> messages,
        int instructionsTokens,
        int effectiveWindowTokens,
        ProviderCallBudgetOptions options)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(options);

        var window = Math.Max(effectiveWindowTokens, 0);
        var estimatedBefore = instructionsTokens + ProviderMessageTokenEstimator.EstimateTokens(messages);

        if (messages.Count == 0 || estimatedBefore <= window)
        {
            return ProviderBudgetResult.Unchanged(messages, estimatedBefore);
        }

        var count = messages.Count;
        var working = new ChatMessage[count];
        var perMessageTokens = new int[count];
        var dropped = new bool[count];
        for (var i = 0; i < count; i++)
        {
            working[i] = messages[i];
            perMessageTokens[i] = ProviderMessageTokenEstimator.EstimateTokens(messages[i]);
        }

        var keepCount = Math.Max(2, options.RecentMessagesToKeep);
        var recentFrom = count - keepCount;
        var excerptChars = Math.Max(0, options.OversizedToolResultExcerptChars);

        var currentEstimate = estimatedBefore;

        // Pass 1: excerpt oversized tool results anywhere (oldest first), including a recent/pending one — excerpting is
        // the primary size backstop and keeps the pending tool result bounded rather than dropping it.
        var toolResultsTruncated = 0;
        var charsTruncated = 0;
        for (var i = 0; i < count && currentEstimate > window; i++)
        {
            if (!TryExcerptToolResult(working[i], excerptChars, out var truncated, out var omitted))
            {
                continue;
            }

            working[i] = truncated;
            var newTokens = ProviderMessageTokenEstimator.EstimateTokens(truncated);
            currentEstimate += newTokens - perMessageTokens[i];
            perMessageTokens[i] = newTokens;
            toolResultsTruncated++;
            charsTruncated += omitted;
        }

        // Pass 2: drop the oldest droppable messages whole. A message is droppable only if it is not a system message,
        // not within the recent-keep window, and not the very last message (the pending tool result is never dropped).
        var messagesDropped = 0;
        for (var i = 0; i < count && currentEstimate > window; i++)
        {
            if (working[i].Role == ChatRole.System || i >= recentFrom || i == count - 1)
            {
                continue;
            }

            dropped[i] = true;
            currentEstimate -= perMessageTokens[i];
            messagesDropped++;
        }

        if (messagesDropped == 0 && toolResultsTruncated == 0)
        {
            // Nothing was reducible (all content is protected/pinned); keep the set and flag the overrun.
            return new ProviderBudgetResult
            {
                Messages = messages,
                Trimmed = false,
                MessagesDropped = 0,
                ToolResultsTruncated = 0,
                CharsTruncated = 0,
                EstimatedTokensBefore = estimatedBefore,
                EstimatedTokensAfter = estimatedBefore,
                ExceedsWindow = estimatedBefore > window
            };
        }

        var survivors = new List<ChatMessage>(count - messagesDropped);
        for (var i = 0; i < count; i++)
        {
            if (!dropped[i])
            {
                survivors.Add(working[i]);
            }
        }

        return new ProviderBudgetResult
        {
            Messages = survivors,
            Trimmed = true,
            MessagesDropped = messagesDropped,
            ToolResultsTruncated = toolResultsTruncated,
            CharsTruncated = charsTruncated,
            EstimatedTokensBefore = estimatedBefore,
            EstimatedTokensAfter = currentEstimate,
            ExceedsWindow = currentEstimate > window
        };
    }

    private static bool TryExcerptToolResult(ChatMessage message, int excerptChars, out ChatMessage truncated, out int charsOmitted)
    {
        truncated = message;
        charsOmitted = 0;

        var isToolMessage = message.Role == ChatRole.Tool;
        List<AIContent>? rewritten = null;

        for (var i = 0; i < message.Contents.Count; i++)
        {
            var content = message.Contents[i];
            AIContent? replacement = null;

            switch (content)
            {
                case FunctionResultContent result:
                    var resultText = result.Result?.ToString();
                    if (resultText is not null && resultText.Length > excerptChars)
                    {
                        var omitted = resultText.Length - excerptChars;
                        replacement = new FunctionResultContent(result.CallId, Excerpt(resultText, excerptChars, omitted))
                        {
                            Exception = result.Exception
                        };
                        charsOmitted += omitted;
                    }

                    break;

                case TextContent text when isToolMessage:
                    var value = text.Text;
                    if (value is not null && value.Length > excerptChars)
                    {
                        var omitted = value.Length - excerptChars;
                        replacement = new TextContent(Excerpt(value, excerptChars, omitted));
                        charsOmitted += omitted;
                    }

                    break;
            }

            if (replacement is not null)
            {
                rewritten ??= [.. message.Contents];
                rewritten[i] = replacement;
            }
        }

        if (rewritten is null)
        {
            return false;
        }

        truncated = new ChatMessage(message.Role, rewritten);
        return true;
    }

    private static string Excerpt(string value, int excerptChars, int omitted)
    {
        var marker = $"[truncated: {omitted} chars omitted]";
        return excerptChars > 0 ? $"{value[..excerptChars]}\n{marker}" : marker;
    }
}

/// <summary>Outcome of one <see cref="ProviderCallBudgeter.Budget" /> pass. Carries no message content, so it is safe to log.</summary>
internal sealed record ProviderBudgetResult
{
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    public required bool Trimmed { get; init; }

    public required int MessagesDropped { get; init; }

    public required int ToolResultsTruncated { get; init; }

    public required int CharsTruncated { get; init; }

    public required int EstimatedTokensBefore { get; init; }

    public required int EstimatedTokensAfter { get; init; }

    /// <summary>True when the message set still exceeds the window after all reductions (the pinned set alone is too big).</summary>
    public required bool ExceedsWindow { get; init; }

    public static ProviderBudgetResult Unchanged(IReadOnlyList<ChatMessage> messages, int estimatedTokens)
    {
        return new ProviderBudgetResult
        {
            Messages = messages,
            Trimmed = false,
            MessagesDropped = 0,
            ToolResultsTruncated = 0,
            CharsTruncated = 0,
            EstimatedTokensBefore = estimatedTokens,
            EstimatedTokensAfter = estimatedTokens,
            ExceedsWindow = false
        };
    }
}
