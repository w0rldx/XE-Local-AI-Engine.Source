namespace XE_Local_AI_Engine.AI.Agent.Chat;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Configuration;

/// <summary>
///     Deterministic, LLM-free reducer that fits a SINGLE raw provider round's message set into the effective context
///     window, used by the provider-boundary budget middleware.
/// </summary>
/// <remarks>
///     Always keeps system messages, the user's current message, the most recent
///     <see cref="ProviderCallBudgetOptions.RecentMessagesToKeep" /> messages and the very last message (the pending tool result the model must see next); over the window it first
///     excerpts oversized tool results anywhere, so the pending result is bounded rather than dropped, then drops the
///     oldest non-protected messages whole, then strips superseded plain reasoning outside the in-flight round. See docs/wiki/04-agent-mode.md ("What the per-round reducer keeps").
/// </remarks>
internal static class ProviderCallBudgeter
{
    public static ProviderBudgetResult Budget(IReadOnlyList<ChatMessage> messages,
        int instructionsTokens,
        int effectiveWindowTokens,
        ProviderCallBudgetOptions options,
        int charsPerToken = 4)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(options);

        var window = Math.Max(effectiveWindowTokens, 0);
        var estimatedBefore = instructionsTokens + ProviderMessageTokenEstimator.EstimateTokens(messages, charsPerToken);

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
            perMessageTokens[i] = ProviderMessageTokenEstimator.EstimateTokens(messages[i], charsPerToken);
        }

        var keepCount = Math.Max(2, options.RecentMessagesToKeep);
        var recentFrom = count - keepCount;

        // The user's current message is irreducible: a few tool rounds push it out of the recent window, and a chat template
        // given no user message rejects the request (llama-server: "No user query found in messages") instead of answering.
        var currentUser = -1;
        for (var i = count - 1; i >= 0 && currentUser < 0; i--)
        {
            if (messages[i].Role == ChatRole.User && !string.IsNullOrWhiteSpace(messages[i].Text))
            {
                currentUser = i;
            }
        }

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
            var newTokens = ProviderMessageTokenEstimator.EstimateTokens(truncated, charsPerToken);
            currentEstimate += newTokens - perMessageTokens[i];
            perMessageTokens[i] = newTokens;
            toolResultsTruncated++;
            charsTruncated += omitted;
        }

        // Pass 2: drop the oldest droppable messages whole, in atomic tool-call/result UNITS — dropping half a pair
        // orphans the other half, which makes OpenAI/Azure reject the round with a 400. One protected member pins a unit.
        var unitRoot = BuildToolCallUnits(working, count);
        var messagesDropped = 0;
        var processedUnits = new HashSet<int>();
        for (var i = 0; i < count && currentEstimate > window; i++)
        {
            if (dropped[i])
            {
                continue;
            }

            var root = Find(unitRoot, i);
            if (!processedUnits.Add(root))
            {
                continue;
            }

            if (!IsUnitDroppable(working, unitRoot, root, count, recentFrom, currentUser))
            {
                continue;
            }

            for (var member = 0; member < count; member++)
            {
                if (dropped[member] || Find(unitRoot, member) != root)
                {
                    continue;
                }

                dropped[member] = true;
                currentEstimate -= perMessageTokens[member];
                messagesDropped++;
            }
        }

        // Pass 3: while still over the window, strip superseded plain reasoning outside the in-flight round (the last message's unit).
        // Provider-backed reasoning (raw item or protected data) stays everywhere: Codex replays it before every retained function_call.
        var reasoningStripped = 0;
        var inFlightUnit = Find(unitRoot, count - 1);
        for (var i = 0; i < count && currentEstimate > window; i++)
        {
            if (dropped[i] || working[i].Role != ChatRole.Assistant || Find(unitRoot, i) == inFlightUnit)
            {
                continue;
            }

            if (!TryStripReasoning(working[i], out var stripped, out var emptied))
            {
                continue;
            }

            reasoningStripped++;

            // Emptied = every part was strippable reasoning: no call id, a unit of its own; sent empty, some providers reject it.
            if (emptied)
            {
                dropped[i] = true;
                currentEstimate -= perMessageTokens[i];
                messagesDropped++;
                continue;
            }

            working[i] = stripped;
            var strippedTokens = ProviderMessageTokenEstimator.EstimateTokens(stripped, charsPerToken);
            currentEstimate += strippedTokens - perMessageTokens[i];
            perMessageTokens[i] = strippedTokens;
        }

        if (messagesDropped == 0 && toolResultsTruncated == 0 && reasoningStripped == 0)
        {
            // Nothing was reducible (all content is protected/pinned); keep the set and flag the overrun.
            return new ProviderBudgetResult
            {
                Messages = messages,
                Trimmed = false,
                MessagesDropped = 0,
                ToolResultsTruncated = 0,
                ReasoningStripped = 0,
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
            ReasoningStripped = reasoningStripped,
            CharsTruncated = charsTruncated,
            EstimatedTokensBefore = estimatedBefore,
            EstimatedTokensAfter = currentEstimate,
            ExceedsWindow = currentEstimate > window
        };
    }

    // Groups messages into atomic tool-call/result units via union-find over shared CallIds; a message chaining two
    // CallIds transitively merges their components. Returns the parent array; resolve a message's unit with Find.
    private static int[] BuildToolCallUnits(IReadOnlyList<ChatMessage> messages, int count)
    {
        var parent = new int[count];
        for (var i = 0; i < count; i++)
        {
            parent[i] = i;
        }

        var callIdToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            foreach (var content in messages[i].Contents)
            {
                var callId = content switch
                {
                    FunctionCallContent call => call.CallId,
                    FunctionResultContent result => result.CallId,
                    _ => null
                };

                if (string.IsNullOrEmpty(callId))
                {
                    continue;
                }

                if (callIdToIndex.TryGetValue(callId, out var existing))
                {
                    Union(parent, existing, i);
                }
                else
                {
                    callIdToIndex[callId] = i;
                }
            }
        }

        return parent;
    }

    private static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
        {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }

        return index;
    }

    private static void Union(int[] parent, int left, int right)
    {
        var rootLeft = Find(parent, left);
        var rootRight = Find(parent, right);
        if (rootLeft != rootRight)
        {
            // Point the higher-index root at the lower one so a unit's canonical root is its oldest message; iteration
            // then encounters the root at the same position it would have dropped the first member.
            if (rootLeft < rootRight)
            {
                parent[rootRight] = rootLeft;
            }
            else
            {
                parent[rootLeft] = rootRight;
            }
        }
    }

    // A unit is droppable only when EVERY member is droppable; a single protected member (system / within the recent-keep
    // window / the last pending message / the user's current message) pins the whole unit so no half of a call/result pair is ever dropped.
    private static bool IsUnitDroppable(IReadOnlyList<ChatMessage> messages, int[] unitRoot, int root, int count, int recentFrom, int currentUser)
    {
        for (var i = 0; i < count; i++)
        {
            if (Find(unitRoot, i) == root && !IsDroppable(messages[i], i, count, recentFrom, currentUser))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDroppable(ChatMessage message, int index, int count, int recentFrom, int currentUser)
    {
        return message.Role != ChatRole.System && index < recentFrom && index != count - 1 && index != currentUser;
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

        truncated = CloneWithContents(message, rewritten);
        return true;
    }

    // Removes every strippable reasoning part; false when the message carries none. emptied = nothing else survives.
    private static bool TryStripReasoning(ChatMessage message, out ChatMessage stripped, out bool emptied)
    {
        stripped = message;
        emptied = false;
        if (!message.Contents.Any(IsStrippableReasoning))
        {
            return false;
        }

        List<AIContent> retained = [.. message.Contents.Where(static content => !IsStrippableReasoning(content))];
        emptied = retained.Count == 0;
        if (!emptied)
        {
            stripped = CloneWithContents(message, retained);
        }

        return true;
    }

    // Plain reasoning only: Chat Completions and Ollama adapters set neither; a Responses item carries a raw item (non-streaming)
    // or protected data (streaming, after coalescing drops the raw item), and Codex must receive it again.
    private static bool IsStrippableReasoning(AIContent content) =>
        content is TextReasoningContent { RawRepresentation: null } reasoning && string.IsNullOrEmpty(reasoning.ProtectedData);

    // Clone-PRESERVING: id, author, provider raw representation and additional properties carry over, because a
    // rewrite from role + contents alone would strip a rewritten message's identity on the way to the provider.
    private static ChatMessage CloneWithContents(ChatMessage message, IList<AIContent> contents)
    {
        return new ChatMessage(message.Role, contents)
        {
            AuthorName = message.AuthorName,
            MessageId = message.MessageId,
            CreatedAt = message.CreatedAt,
            RawRepresentation = message.RawRepresentation,
            AdditionalProperties = message.AdditionalProperties
        };
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

    /// <summary>Messages whose superseded reasoning was stripped (a reasoning-only one is also counted in <see cref="MessagesDropped" />).</summary>
    public required int ReasoningStripped { get; init; }

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
            ReasoningStripped = 0,
            CharsTruncated = 0,
            EstimatedTokensBefore = estimatedTokens,
            EstimatedTokensAfter = estimatedTokens,
            ExceedsWindow = false
        };
    }
}
