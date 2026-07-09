namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

/// <summary>
///     Deterministic, LLM-free implementation of <see cref="IConversationContextBudgeter" />. Groups the history into
///     turns (a user message and every assistant/tool message that follows it up to the next user message), always keeps
///     system messages and the most recent <see cref="ConversationContextBudgetOptions.RecentTurnKeepCount" /> turns,
///     and when the estimate exceeds the budget reduces in two ordered passes: first shorten oversized historical tool
///     results to an excerpt, then drop the oldest turns whole. Because tool-call and tool-result messages of one turn
///     share a turn index, dropping a turn never orphans a tool-call from its result, and the protected recent turns —
///     which contain the in-flight tool-calling round — are never modified.
/// </summary>
public sealed class ConversationContextBudgeter : IConversationContextBudgeter
{
    private readonly ITokenEstimator _estimator;
    private readonly ConversationContextBudgetOptions _options;

    public ConversationContextBudgeter(ITokenEstimator estimator, IOptions<ConversationContextBudgetOptions> options)
    {
        _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public ConversationBudgetResult Budget(IReadOnlyList<ChatMessage> messages, int contextTokenCapacity, int reservedOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var effectiveBudget = Math.Max(contextTokenCapacity - reservedOutputTokens, 0);
        var estimatedBefore = _estimator.EstimateTokens(messages);

        if (messages.Count == 0 || estimatedBefore <= effectiveBudget)
        {
            return ConversationBudgetResult.Unchanged(messages, estimatedBefore);
        }

        var count = messages.Count;
        var working = new ChatMessage[count];
        var perMessageTokens = new int[count];
        var turnOf = new int[count];
        var dropped = new bool[count];

        var turn = 0;
        var sawUser = false;
        for (var i = 0; i < count; i++)
        {
            var message = messages[i];
            working[i] = message;
            perMessageTokens[i] = _estimator.EstimateTokens(message);

            if (message.Role == ChatRole.User)
            {
                if (sawUser)
                {
                    turn++;
                }

                sawUser = true;
            }

            turnOf[i] = turn;
        }

        var maxTurn = turn;
        var keepCount = Math.Max(1, _options.RecentTurnKeepCount);

        // Turns with an index at or above this threshold are the protected recent window: always kept, never modified.
        // A non-positive threshold means every turn is within the keep window (nothing is droppable).
        var protectedFrom = maxTurn - keepCount + 1;

        var currentEstimate = estimatedBefore;

        // Pass 1: shorten oversized historical tool results (oldest first) before any whole turn is dropped.
        var toolResultsTruncated = 0;
        var charsTruncated = 0;
        for (var i = 0; i < count && currentEstimate > effectiveBudget; i++)
        {
            if (turnOf[i] >= protectedFrom || working[i].Role == ChatRole.System)
            {
                continue;
            }

            if (!TryTruncateToolResult(working[i], out var truncated, out var omitted))
            {
                continue;
            }

            working[i] = truncated;
            var newTokens = _estimator.EstimateTokens(truncated);
            currentEstimate += newTokens - perMessageTokens[i];
            perMessageTokens[i] = newTokens;
            toolResultsTruncated++;
            charsTruncated += omitted;
        }

        // Pass 2: drop the oldest droppable turns whole; system messages stay pinned even inside a dropped turn.
        var messagesDropped = 0;
        for (var t = 0; t < protectedFrom && currentEstimate > effectiveBudget; t++)
        {
            for (var i = 0; i < count; i++)
            {
                if (dropped[i] || turnOf[i] != t || working[i].Role == ChatRole.System)
                {
                    continue;
                }

                dropped[i] = true;
                currentEstimate -= perMessageTokens[i];
                messagesDropped++;
            }
        }

        var survivors = new List<ChatMessage>(count - messagesDropped);
        for (var i = 0; i < count; i++)
        {
            if (!dropped[i])
            {
                survivors.Add(working[i]);
            }
        }

        return new ConversationBudgetResult
        {
            Messages = survivors,
            Trimmed = messagesDropped > 0 || toolResultsTruncated > 0,
            MessagesDropped = messagesDropped,
            ToolResultsTruncated = toolResultsTruncated,
            CharsTruncated = charsTruncated,
            EstimatedTokensBefore = estimatedBefore,
            EstimatedTokensAfter = currentEstimate,
            ExceedsBudget = currentEstimate > effectiveBudget
        };
    }

    /// <summary>
    ///     Rewrites a message whose tool-result content exceeds the excerpt budget, replacing each oversized result with
    ///     a leading excerpt plus an explicit omitted-count marker. Truncates <see cref="FunctionResultContent" />
    ///     anywhere and, for a <see cref="ChatRole.Tool" /> message, its plain <see cref="TextContent" /> (history that
    ///     arrives as a tool-role text message rather than a structured result). Returns false when nothing was oversized.
    /// </summary>
    private bool TryTruncateToolResult(ChatMessage message, out ChatMessage truncated, out int charsOmitted)
    {
        truncated = message;
        charsOmitted = 0;

        var excerptChars = Math.Max(0, _options.HistoricalToolResultExcerptChars);
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
