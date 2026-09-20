namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

/// <summary>
///     Deterministic, LLM-free <see cref="IConversationContextBudgeter" />: groups the history into turns and reduces
///     it in ordered passes until the estimate fits the budget.
/// </summary>
/// <remarks>
///     A turn is a user message plus every assistant/tool message up to the next user message, so a tool call and its
///     result share a turn index and dropping a turn never orphans one. System messages and tool-approval correlation
///     records are always kept. Passes 1-3 excerpt historical tool results, drop whole historical turns, then evict
///     whole historical approval groups; Passes 4 and 5 are opt-in and reached only when the PROTECTED recent window
///     alone is what does not fit. Each pass documents itself at its own site, and every rewrite is clone-preserving.
/// </remarks>
public sealed class ConversationContextBudgeter : IConversationContextBudgeter
{
    /// <summary>The framed <see cref="ChatMessage" /> each fixed-overhead text is measured as, memoized by TEXT INSTANCE.</summary>
    /// <remarks>
    ///     <see cref="ITokenEstimator" />'s own character-profile memo is keyed on the message instance, so a fresh
    ///     wrapper per call always missed and re-scanned the whole system prompt — tens of KB once skills are attached —
    ///     on every call. Callers pass the same string instances for the life of an invocation, so this turns those
    ///     repeated scans into lookups. No leak: an entry dies with its key string.
    /// </remarks>
    private static readonly ConditionalWeakTable<string, ChatMessage> FixedOverheadFramingCache = new();

    private readonly ITokenEstimator _estimator;
    private readonly ConversationContextBudgetOptions _options;

    public ConversationContextBudgeter(ITokenEstimator estimator, IOptions<ConversationContextBudgetOptions> options)
    {
        _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public ConversationBudgetResult Budget(IReadOnlyList<ChatMessage> messages,
        int contextTokenCapacity,
        int reservedOutputTokens,
        string? systemPrompt = null,
        IReadOnlyList<string>? toolDefinitions = null,
        string? modelName = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // The system prompt is prepended AFTER this history and tool JSON schemas never appear in the message list, yet both count against the launched window.
        // Folding their estimate in stops the budget being measured against history alone; it mirrors the inner budgeter, over-counting slightly, the safe direction.
        var divisor = _estimator.ResolveDivisor(modelName);
        var fixedOverhead = EstimateFixedOverhead(systemPrompt, toolDefinitions, divisor);
        // Same margins the inner provider-round budgeter applies, from the same constants: the shared char heuristic under-counts on markdown and JSON, and an
        // under-count at the window edge is a provider rejection rather than a trim. The flat factor covers an unknown model; the observed correction is tighten-only.
        var observedCorrection = _estimator.ResolveObservedCorrection(modelName);
        var effectiveBudget = Math.Max(TokenEstimatorCalibrationStore.ApplyEstimateMargins(contextTokenCapacity, observedCorrection)
                                       - reservedOutputTokens
                                       - fixedOverhead,
            0);
        var estimatedBefore = _estimator.EstimateTokensWithDivisor(messages, divisor);

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
            perMessageTokens[i] = _estimator.EstimateTokensWithDivisor(message, divisor);

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

        // Approval correlation records outlive the turn window — each replayed decision is its own User message, so a many-tool turn puts its resolved rounds far
        // outside the keep window — and the validator fails the invocation on a request without its response, or a resolved response without the result it produced.
        var approvals = BuildApprovalGroups(messages);

        // Pinning is per message, not per turn, so the surrounding history still trims.
        var pinned = approvals.Pinned;

        // Floor of 2 even against a mis-set config: the approval-replay path splits one in-flight round across two turns, the assistant tool-call and its request
        // in turn M and the replayed User decision in turn M+1, so protecting one turn could drop M and orphan the response. Options validation also enforces it.
        var keepCount = Math.Max(2, _options.RecentTurnKeepCount);

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
            var newTokens = _estimator.EstimateTokensWithDivisor(truncated, divisor);
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
                if (dropped[i] || turnOf[i] != t || working[i].Role == ChatRole.System || (pinned is not null && pinned[i]))
                {
                    continue;
                }

                dropped[i] = true;
                currentEstimate -= perMessageTokens[i];
                messagesDropped++;
            }
        }

        // Pass 3: the approval pins are a correlation guarantee, not a reservation — left permanent they accumulate until the pinned set alone exceeds the budget and
        // the hard stop rejects every later turn. A group (request, decision, the results it produced) is evicted atomically, oldest first; an in-flight one never is.
        for (var g = 0; g < approvals.Groups.Count && currentEstimate > effectiveBudget; g++)
        {
            var group = approvals.Groups[g];
            if (!group.Complete || !IsEvictable(group, turnOf, working, dropped, protectedFrom))
            {
                continue;
            }

            foreach (var i in group.MessageIndices)
            {
                dropped[i] = true;
                currentEstimate -= perMessageTokens[i];
                messagesDropped++;
            }
        }

        // Passes 4 and 5 are the last-resort reclaims, reached ONLY because Passes 1-3 left the round over budget — which, once Pass 3 evicted everything
        // historical, means the PROTECTED window itself does not fit. Both work on SURVIVORS only and stop the moment it fits; the alternative is the hard stop.
        var lastSurvivor = -1;

        // The LAST surviving message is exempt from both: it is the in-flight round the model is producing against — the pending tool result,
        // or the approval decision just replayed — and the one thing the next provider call cannot be asked to work without.
        for (var i = count - 1; i >= 0; i--)
        {
            if (!dropped[i])
            {
                lastSurvivor = i;
                break;
            }
        }

        // Pass 4: strip the model's own superseded reasoning, oldest first, only while still over budget. Reasoning is dead scratch-pad text once a later round
        // supersedes it and neither the approval validator nor the tool-call correlation inspects it, so removing it orphans nothing; system messages are excluded.
        var reasoningStripped = 0;
        if (_options.StripProtectedReasoning)
        {
            for (var i = 0; i < count && currentEstimate > effectiveBudget; i++)
            {
                if (dropped[i] || i == lastSurvivor || working[i].Role == ChatRole.System)
                {
                    continue;
                }

                if (!TryStripReasoning(working[i], out var stripped, out var emptied))
                {
                    continue;
                }

                reasoningStripped++;

                // A reasoning-ONLY message has nothing left to say once its reasoning is gone. Sending an empty message
                // is a shape some providers reject outright and none can use, so it goes whole.
                if (emptied)
                {
                    dropped[i] = true;
                    currentEstimate -= perMessageTokens[i];
                    messagesDropped++;
                    continue;
                }

                working[i] = stripped;
                var strippedTokens = _estimator.EstimateTokensWithDivisor(stripped, divisor);
                currentEstimate += strippedTokens - perMessageTokens[i];
                perMessageTokens[i] = strippedTokens;
            }
        }

        // Pass 5: excerpt tool results inside the PROTECTED window — the one pass that shortens content the current round is actively working with, hence opt-in.
        // Restricted there because Pass 1 has already visited every historical message; excerpting preserves the CallId the validator matches on.
        var protectedResultsExcerpted = 0;
        if (_options.ExcerptProtectedToolResults)
        {
            for (var i = 0; i < count && currentEstimate > effectiveBudget; i++)
            {
                if (dropped[i] || i == lastSurvivor || turnOf[i] < protectedFrom || working[i].Role == ChatRole.System)
                {
                    continue;
                }

                if (!TryTruncateToolResult(working[i], out var excerpted, out var protectedOmitted))
                {
                    continue;
                }

                working[i] = excerpted;
                var excerptedTokens = _estimator.EstimateTokensWithDivisor(excerpted, divisor);
                currentEstimate += excerptedTokens - perMessageTokens[i];
                perMessageTokens[i] = excerptedTokens;
                protectedResultsExcerpted++;
                charsTruncated += protectedOmitted;
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

        // Measured on the MATERIALIZED survivor list rather than the running total, which five passes add to, subtract from and rewrite. Re-estimating the exact
        // list about to be sent is what makes EstimatedTokensAfter — and the ExceedsBudget hard stop derived from it — a statement about the payload.
        var estimatedAfter = _estimator.EstimateTokensWithDivisor(survivors, divisor);

        return new ConversationBudgetResult
        {
            Messages = survivors,
            Trimmed = messagesDropped > 0 || toolResultsTruncated > 0 || reasoningStripped > 0 || protectedResultsExcerpted > 0,
            MessagesDropped = messagesDropped,
            ToolResultsTruncated = toolResultsTruncated,
            ReasoningStrippedCount = reasoningStripped,
            ProtectedResultsExcerptedCount = protectedResultsExcerpted,
            CharsTruncated = charsTruncated,
            EstimatedTokensBefore = estimatedBefore,
            EstimatedTokensAfter = estimatedAfter,
            ExceedsBudget = estimatedAfter > effectiveBudget
        };
    }

    /// <summary>
    ///     Flags the messages Pass 2 must never drop, and partitions those same messages into the correlated groups
    ///     Pass 3 may evict whole.
    /// </summary>
    /// <remarks>
    ///     Pinned are the messages carrying a <see cref="ToolApprovalRequestContent" /> or <see cref="ToolApprovalResponseContent" />, plus every message
    ///     holding a <see cref="FunctionResultContent" /> for one of their calls, since a resolved round's response is only replayable while its result is in
    ///     the batch. A group is a connected component over "shares a request id or a call id" — a call id can be blank, a request id never is — ordered by
    ///     oldest message. Pass 1 is exempt: it cannot touch an approval record, and excerpting preserves the CallId the validator matches on.
    /// </remarks>
    private static ApprovalCorrelation BuildApprovalGroups(IReadOnlyList<ChatMessage> messages)
    {
        // Everything here stays null until the first approval content is seen, so the common approval-free history
        // still costs exactly one scan and no allocation.
        bool[]? pinned = null;
        MessageUnion? union = null;
        Dictionary<string, int>? messageOfRequestId = null;
        Dictionary<string, int>? messageOfCallId = null;
        HashSet<int>? decided = null;

        for (var i = 0; i < messages.Count; i++)
        {
            foreach (var content in messages[i].Contents)
            {
                // A request and the decision replayed for it correlate on REQUEST id, the id the approval validator matches them on and the only one always
                // present. A blank CallId is a supported shape, and correlating on it would leave such a pair in separate groups — the orphan this prevents.
                var (requestId, toolCall) = content switch
                {
                    ToolApprovalRequestContent request => (request.RequestId, request.ToolCall),
                    ToolApprovalResponseContent response => (response.RequestId, response.ToolCall),
                    _ => ((string?)null, (ToolCallContent?)null)
                };

                if (string.IsNullOrEmpty(requestId))
                {
                    continue;
                }

                pinned ??= new bool[messages.Count];
                union ??= new MessageUnion(messages.Count);
                pinned[i] = true;

                Correlate(ref messageOfRequestId, union, requestId, i);

                // CallId is recorded ONLY so the FunctionResultContent sweep below can find its round. It is not the
                // request/response link, and it is legitimately empty.
                if (toolCall is not null && !string.IsNullOrEmpty(toolCall.CallId))
                {
                    Correlate(ref messageOfCallId, union, toolCall.CallId, i);
                }

                // Only a replayed decision makes a round historical. A request still awaiting one is the in-flight
                // round, and evicting it would delete an approval the user has not answered yet.
                if (content is ToolApprovalResponseContent)
                {
                    decided ??= [];
                    _ = decided.Add(i);
                }
            }
        }

        if (pinned is null || union is null)
        {
            return new ApprovalCorrelation { Pinned = null, Groups = [] };
        }

        if (messageOfCallId is not null)
        {
            for (var i = 0; i < messages.Count; i++)
            {
                foreach (var content in messages[i].Contents)
                {
                    if (content is FunctionResultContent result && messageOfCallId.TryGetValue(result.CallId, out var owner))
                    {
                        pinned[i] = true;
                        union.Merge(i, owner);
                    }
                }
            }
        }

        return new ApprovalCorrelation { Pinned = pinned, Groups = union.CollectGroups(pinned, decided) };
    }

    /// <summary>
    ///     Records the first message an id was seen on, or joins this message to the one that already owns it. The
    ///     dictionary is created on first use so an approval-free history allocates nothing.
    /// </summary>
    private static void Correlate(ref Dictionary<string, int>? messageOfId, MessageUnion union, string id, int messageIndex)
    {
        messageOfId ??= new Dictionary<string, int>(StringComparer.Ordinal);
        if (messageOfId.TryGetValue(id, out var owner))
        {
            union.Merge(messageIndex, owner);
        }
        else
        {
            messageOfId[id] = messageIndex;
        }
    }

    /// <summary>
    ///     Whether every message of an approval group may be dropped: none a system message, none still inside the
    ///     protected recent window, none already dropped by an earlier pass.
    /// </summary>
    /// <remarks>Whole group or nothing — a partial eviction is precisely the orphan the pins exist to prevent.</remarks>
    private static bool IsEvictable(ApprovalGroup group, int[] turnOf, ChatMessage[] working, bool[] dropped, int protectedFrom)
    {
        foreach (var i in group.MessageIndices)
        {
            if (dropped[i] || turnOf[i] >= protectedFrom || working[i].Role == ChatRole.System)
            {
                return false;
            }
        }

        return true;
    }

    private sealed record ApprovalCorrelation
    {
        public required bool[]? Pinned { get; init; }

        public required IReadOnlyList<ApprovalGroup> Groups { get; init; }
    }

    /// <summary>One tool-approval round as the budgeter must treat it: an all-or-nothing set of message indices.</summary>
    private sealed record ApprovalGroup
    {
        public required IReadOnlyList<int> MessageIndices { get; init; }

        public required bool Complete { get; init; }
    }

    /// <summary>
    ///     Disjoint-set over message indices, merged whenever two messages share a tool-call id. A message carrying
    ///     several rounds' content pulls all of them into one group, which is what stops an eviction from taking half
    ///     of a message's correlations.
    /// </summary>
    private sealed class MessageUnion
    {
        private readonly int[] _parent;

        public MessageUnion(int count)
        {
            _parent = [.. Enumerable.Range(0, count)];
        }

        public void Merge(int left, int right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot != rightRoot)
            {
                // Lowest index wins the root, so the group's oldest message is also its identity.
                _parent[Math.Max(leftRoot, rightRoot)] = Math.Min(leftRoot, rightRoot);
            }
        }

        /// <summary>Buckets the approval-correlated messages into groups, oldest group first (Pass 3's eviction order).</summary>
        public IReadOnlyList<ApprovalGroup> CollectGroups(bool[] pinned, HashSet<int>? decided)
        {
            var members = new SortedDictionary<int, List<int>>();
            var complete = new HashSet<int>();
            for (var i = 0; i < pinned.Length; i++)
            {
                if (!pinned[i])
                {
                    continue;
                }

                var root = Find(i);
                if (!members.TryGetValue(root, out var bucket))
                {
                    members[root] = bucket = [];
                }

                bucket.Add(i);
                if (decided?.Contains(i) == true)
                {
                    _ = complete.Add(root);
                }
            }

            return [.. members.Select(entry => new ApprovalGroup { MessageIndices = entry.Value, Complete = complete.Contains(entry.Key) })];
        }

        private int Find(int index)
        {
            while (_parent[index] != index)
            {
                _parent[index] = _parent[_parent[index]];
                index = _parent[index];
            }

            return index;
        }
    }

    /// <summary>
    ///     Estimates the fixed per-round input overhead that never appears as a droppable history message but still
    ///     counts against the context window, or 0 when there is none.
    /// </summary>
    /// <remarks>
    ///     That is the resolved system prompt, measured as a System message, plus the model-facing definition text of
    ///     each advertised tool, measured as one framed unit each. It reuses the injected <see cref="ITokenEstimator" />
    ///     — deliberately the same conservative, upper-biased estimator the history uses — mirroring the inner
    ///     <c>ProviderCallBudgetChatClient</c>'s Instructions + Tools estimate, so outer and inner size a round alike.
    /// </remarks>
    private int EstimateFixedOverhead(string? systemPrompt, IReadOnlyList<string>? toolDefinitions, int divisor)
    {
        var overhead = 0;

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            overhead += _estimator.EstimateTokensWithDivisor(AsFramedMessage(systemPrompt), divisor);
        }

        if (toolDefinitions is { Count: > 0 })
        {
            foreach (var definition in toolDefinitions)
            {
                if (string.IsNullOrEmpty(definition))
                {
                    continue;
                }

                // One framed message per tool mirrors the inner estimator's per-tool framing overhead, so a tool-heavy
                // agent's schema footprint is counted rather than silently ignored.
                overhead += _estimator.EstimateTokensWithDivisor(AsFramedMessage(definition), divisor);
            }
        }

        return overhead;
    }

    /// <summary>Frames one fixed-overhead text as the System message it is measured as, reusing the instance per text.</summary>
    private static ChatMessage AsFramedMessage(string text)
    {
        return FixedOverheadFramingCache.GetValue(text, static value => new ChatMessage(ChatRole.System, value));
    }

    /// <summary>
    ///     Rewrites a message whose tool-result content exceeds the excerpt budget, replacing each oversized result
    ///     with a leading excerpt plus an explicit omitted-count marker.
    /// </summary>
    /// <remarks>
    ///     Truncates <see cref="FunctionResultContent" /> anywhere and, for a <see cref="ChatRole.Tool" /> message, its
    ///     plain <see cref="TextContent" /> — history arriving as a tool-role text message rather than a structured
    ///     result. Returns false when nothing was oversized. Shared by Pass 1 and Pass 5 so both produce byte-identical
    ///     excerpt markers, and the rewrite is clone-preserving (<see cref="CloneWithContents" />).
    /// </remarks>
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

        truncated = CloneWithContents(message, rewritten);
        return true;
    }

    /// <summary>Removes every <see cref="TextReasoningContent" /> part from a message (Pass 4).</summary>
    /// <remarks>
    ///     Returns false when the message carries none, so an unaffected message is neither re-allocated nor counted.
    ///     <paramref name="emptied" /> reports the reasoning-ONLY case: nothing survives the strip, so the caller drops
    ///     the message whole instead of sending a contentless one.
    /// </remarks>
    private static bool TryStripReasoning(ChatMessage message, out ChatMessage stripped, out bool emptied)
    {
        stripped = message;
        emptied = false;

        // Stays null until the first reasoning part is seen: the overwhelmingly common no-reasoning message allocates
        // nothing and returns after one scan.
        List<AIContent>? retained = null;
        for (var i = 0; i < message.Contents.Count; i++)
        {
            var content = message.Contents[i];
            if (content is not TextReasoningContent)
            {
                retained?.Add(content);
                continue;
            }

            if (retained is null)
            {
                retained = new List<AIContent>(message.Contents.Count);
                for (var earlier = 0; earlier < i; earlier++)
                {
                    retained.Add(message.Contents[earlier]);
                }
            }
        }

        if (retained is null)
        {
            return false;
        }

        emptied = retained.Count == 0;
        if (!emptied)
        {
            stripped = CloneWithContents(message, retained);
        }

        return true;
    }

    /// <summary>Rewrites a message's content list while PRESERVING its identity.</summary>
    /// <remarks>
    ///     Id, author, provider raw representation and additional properties all carry over. Reconstructing from role
    ///     plus contents alone drops them, so a truncated or reasoning-stripped message stops being the same message to
    ///     anything downstream that keys on <see cref="ChatMessage.MessageId" /> or reads provider metadata off
    ///     <see cref="ChatMessage.RawRepresentation" />.
    /// </remarks>
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

    /// <summary>The excerpt marker every truncation in this repository shares.</summary>
    /// <remarks>
    ///     Internal rather than private because the replayed tool history is capped at PROJECTION time
    ///     (<c>ConversationContextBuilder.Build</c>), before this budgeter sees the round, and a second marker shape
    ///     would make two truncations of the same result read as two different results.
    /// </remarks>
    internal static string Excerpt(string value, int excerptChars, int omitted)
    {
        var marker = $"[truncated: {omitted} chars omitted]";
        return excerptChars > 0 ? $"{value[..excerptChars]}\n{marker}" : marker;
    }
}
