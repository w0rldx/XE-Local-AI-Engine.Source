namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

/// <summary>
///     Deterministic, LLM-free implementation of <see cref="IConversationContextBudgeter" />. Groups the history into
///     turns (a user message and every assistant/tool message that follows it up to the next user message), always keeps
///     system messages and tool-approval correlation records, and when the estimate exceeds the budget reduces in
///     ordered passes: (1) shorten oversized HISTORICAL tool results to an excerpt, (2) drop the oldest historical turns
///     whole, (3) evict whole historical approval groups oldest-first. Because tool-call and tool-result messages of one
///     turn share a turn index, dropping a turn never orphans a tool-call from its result.
/// <para>
///     Two further passes exist only for the rounds those three cannot rescue — the ones that would otherwise raise
///     <see cref="ContextBudgetExceededException" /> and fail the turn outright, which in practice means the PROTECTED
///     recent window alone exceeds the budget and there is no older history left to reclaim. Both are opt-in
///     (<see cref="ConversationContextBudgetOptions.StripProtectedReasoning" />,
///     <see cref="ConversationContextBudgetOptions.ExcerptProtectedToolResults" />), both act on SURVIVORS only, both
///     work oldest-first and stop the moment the round fits, and neither ever touches the last surviving message:
///     (4) strip <see cref="TextReasoningContent" /> — superseded scratch-pad thinking, never a call/result/approval
///     record, so no correlation can be orphaned; a message that carried nothing else is dropped whole rather than sent
///     empty — and (5) excerpt tool results inside the protected window with the same marker Pass 1 uses. Rewrites are
///     clone-PRESERVING (id, author, raw representation, additional properties survive), so a rewritten message is still
///     the same message to everything downstream.
/// </para>
/// </summary>
public sealed class ConversationContextBudgeter : IConversationContextBudgeter
{
    /// <summary>
    ///     The framed <see cref="ChatMessage" /> each fixed-overhead text is measured as, memoized by TEXT INSTANCE.
    ///     <see cref="ITokenEstimator" />'s own character-profile memo is keyed on the message instance, so building a
    ///     fresh wrapper per call guaranteed a miss and re-scanned the whole system prompt (tens of KB once skills are
    ///     attached) on every call. The callers pass the same string instances for the life of an invocation, so this
    ///     turns the repeated scans into lookups. No leak: an entry dies with its key string.
    /// </summary>
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

        // ORC-02: the system prompt is prepended to the request AFTER this history, and tool JSON schemas never appear
        // in the message list at all — yet both count against the launched window. Folding their estimate into the
        // effective budget stops the outer budget/hard-stop from being measured against history alone (which would let
        // an actually-over-window request through, deferring to a late inner rejection). Mirrors the inner
        // ProviderCallBudgetChatClient's Instructions + Tools overhead so the two budgeters approximately agree (the
        // outer estimate over-counts slightly by framing each tool as its own System message — the safe direction).
        var divisor = _estimator.ResolveDivisor(modelName);
        var fixedOverhead = EstimateFixedOverhead(systemPrompt, toolDefinitions, divisor);
        // Same margins the inner provider-round budgeter applies, for the same reason and from the same constants: the
        // shared char heuristic under-counts on markdown and JSON, and an under-count at the window edge is a provider
        // rejection rather than a trim. The flat safety factor covers the first round of an unknown model; the observed
        // correction is what real rounds of THIS model have since taught (tighten-only, neutral until then, so a model
        // nothing has been recorded for is byte-identical to before). Comparison only.
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

        // Approval correlation records outlive the turn window. FunctionInvokingChatClient pairs each
        // ToolApprovalRequestContent with the ToolApprovalResponseContent replayed for it, and the approval validator
        // fails the whole invocation when a replayed batch carries one without the other — or, for an already-resolved
        // round, without the FunctionResultContent that decision produced. Every replayed decision is its own
        // ChatRole.User message, so a turn using many approved tools puts each resolved round in its own turn, far
        // outside the keep window the floor below protects; whole-turn dropping would then split pairs that must stay
        // together. Pinning is per message, not per turn, so the surrounding history still trims.
        var approvals = BuildApprovalGroups(messages);
        var pinned = approvals.Pinned;

        // Floor of 2, even against a mis-set config: the approval-replay path splits one in-flight round across two
        // turns — the assistant tool-call (and its approval request) land in turn M, and the User approval-decision
        // that FunctionInvokingChatClient replays lands in turn M+1. Protecting only one turn could drop turn M and
        // orphan the approval response. Options validation also enforces >= 2; this clamp is the defence in depth.
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

        // Pass 3: the approval pins are a correlation guarantee, not a reservation. Left permanent they accumulate
        // without bound, and an approval-heavy conversation eventually has a pinned set that alone exceeds the budget —
        // at which point ExceedsBudget is stuck true and the runner's hard stop rejects EVERY later turn, permanently.
        // So once the ordinary passes are spent, evict whole historical approval groups oldest first. A group is the
        // request, its replayed decision, and the results that decision produced, all correlated by call id; taking it
        // atomically is what keeps the validator satisfied, which fails a response whose request is missing and a
        // resolved response whose FunctionResultContent is missing. An incomplete group (a surfaced request with no
        // decision yet — the in-flight round) is never a candidate: there is nothing historical about it.
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

        // Passes 4 and 5 are the last-resort reclaims, and they run ONLY because Passes 1-3 left the round over budget —
        // which, once Pass 3 has evicted everything historical it may, means the PROTECTED window itself is what does not
        // fit. No amount of further older-history compaction (deterministic or LLM-summarized) can reach that; the
        // alternative to these two passes is the hard stop below, i.e. failing the turn outright. Both work on SURVIVORS
        // only (a message an earlier pass already dropped is not rewritten and never re-counted) and both stop the moment
        // the round fits, so a round that Passes 1-3 rescued pays nothing here.
        //
        // The LAST surviving message is exempt from both: it is the in-flight round the model is producing against (the
        // pending tool result, or the approval decision just replayed), and it is the one thing the next provider call
        // cannot be asked to work without.
        var lastSurvivor = -1;
        for (var i = count - 1; i >= 0; i--)
        {
            if (!dropped[i])
            {
                lastSurvivor = i;
                break;
            }
        }

        // Pass 4: strip the model's own superseded reasoning, oldest first, only while still over budget. Reasoning is
        // dead scratch-pad text once a later round supersedes it, and neither the approval validator nor the tool-call
        // correlation (BuildApprovalGroups / FunctionResultContent CallIds) ever inspects it — so removing it can orphan
        // nothing. System messages are excluded, keeping "system messages are never modified" absolute.
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

        // Pass 5: excerpt tool results inside the PROTECTED window — the one pass that shortens content the current round
        // is actively working with, hence opt-in. Restricted to the protected region because Pass 1 has already visited
        // every historical message (it runs under the same while-over-budget condition, so reaching here means it ran to
        // the end of the list). Excerpting preserves the CallId the validator matches on, so the correlation survives.
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

        // Measured on the MATERIALIZED survivor list rather than reported from the running total. The running total is
        // maintained incrementally across five passes that add, subtract and rewrite; re-estimating the exact list that
        // is about to be sent is what makes EstimatedTokensAfter — and the ExceedsBudget hard stop derived from it — a
        // statement about the payload rather than about the bookkeeping.
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
    ///     Flags the messages Pass 2 must never drop because doing so would break a tool-approval correlation — the
    ///     messages carrying a <see cref="ToolApprovalRequestContent" /> or <see cref="ToolApprovalResponseContent" />,
    ///     plus every message holding a <see cref="FunctionResultContent" /> for one of their tool calls (a resolved
    ///     round's response is only replayable while its result is still in the batch) — and partitions those same
    ///     messages into the correlated GROUPS Pass 3 may then evict whole. Both come from one scan; the pin array is
    ///     null when the history holds no approval content at all, which is the common case.
    ///     <para>
    ///         A group is a connected component over "shares a request id or a call id" — request id links a request to
    ///         its replayed decision (a call id can be blank, a request id never is), call id attaches the results that
    ///         decision produced. A message carrying several rounds' content merges them rather than splitting a
    ///         correlation across two evictions. Groups are ordered by their oldest message, which is the order Pass 3
    ///         evicts in.
    ///     </para>
    ///     <para>
    ///         Deliberately NOT applied to Pass 1: the approval records themselves hold neither a tool result nor
    ///         tool-role text, so excerpting cannot touch them, while the paired results must stay excerptable or a
    ///         long approved-tool turn would leave the budget nothing to reclaim. Excerpting preserves the CallId the
    ///         validator matches on, so it never breaks the correlation.
    ///     </para>
    /// </summary>
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
                // A request and the decision replayed for it correlate on REQUEST id — the id the approval validator
                // matches them on, and the only one always present. A blank CallId is a supported shape (an approval
                // surfaced for a call that has none); correlating on CallId left such a request and its response in
                // separate groups, so Pass 3 could take the decision and keep the request — the orphan this whole
                // mechanism exists to prevent.
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
            return new ApprovalCorrelation(null, []);
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

        return new ApprovalCorrelation(pinned, union.CollectGroups(pinned, decided));
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
    ///     True when every message of an approval group may be dropped: none is a system message, none is still inside
    ///     the protected recent window, and none was already dropped by an earlier pass. Whole group or nothing — a
    ///     partial eviction is precisely the orphan the pins exist to prevent.
    /// </summary>
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

    private sealed record ApprovalCorrelation(bool[]? Pinned, IReadOnlyList<ApprovalGroup> Groups);

    /// <summary>One tool-approval round as the budgeter must treat it: an all-or-nothing set of message indices.</summary>
    private sealed record ApprovalGroup(IReadOnlyList<int> MessageIndices, bool Complete);

    /// <summary>
    ///     Disjoint-set over message indices, merged whenever two messages share a tool-call id. A message carrying
    ///     several rounds' content pulls all of them into one group, which is what stops an eviction from taking half
    ///     of a message's correlations.
    /// </summary>
    private sealed class MessageUnion(int count)
    {
        private readonly int[] _parent = [.. Enumerable.Range(0, count)];

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

            return [.. members.Select(entry => new ApprovalGroup(entry.Value, complete.Contains(entry.Key)))];
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
    ///     ORC-02: estimates the fixed per-round input overhead that never appears as a droppable history message but
    ///     still counts against the context window — the resolved system prompt (measured as a System message) plus the
    ///     model-facing definition text of each advertised tool (measured as one framed unit each). Reuses the injected
    ///     <see cref="ITokenEstimator" /> (deliberately the same conservative, upper-biased estimator the history uses),
    ///     mirroring the inner <c>ProviderCallBudgetChatClient</c>'s Instructions + Tools estimate so the outer and inner
    ///     budgeters size the same round the same way. Returns 0 when both are absent.
    /// </summary>
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
    ///     Rewrites a message whose tool-result content exceeds the excerpt budget, replacing each oversized result with
    ///     a leading excerpt plus an explicit omitted-count marker. Truncates <see cref="FunctionResultContent" />
    ///     anywhere and, for a <see cref="ChatRole.Tool" /> message, its plain <see cref="TextContent" /> (history that
    ///     arrives as a tool-role text message rather than a structured result). Returns false when nothing was oversized.
    ///     Shared by Pass 1 (historical results) and Pass 5 (protected-window results) so both produce byte-identical
    ///     excerpt markers. The rewrite is clone-preserving (see <see cref="CloneWithContents" />).
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

        truncated = CloneWithContents(message, rewritten);
        return true;
    }

    /// <summary>
    ///     Removes every <see cref="TextReasoningContent" /> part from a message (Pass 4). Returns false when the message
    ///     carries none, so an unaffected message is neither re-allocated nor counted.
    ///     <paramref name="emptied" /> reports the reasoning-ONLY case: nothing survives the strip, so the caller drops the
    ///     message whole instead of sending a contentless one.
    /// </summary>
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

    /// <summary>
    ///     Rewrites a message's content list while PRESERVING its identity — id, author, provider raw representation and
    ///     additional properties all carry over. Reconstructing from role + contents alone (which every rewrite here used
    ///     to do) silently dropped those, so a truncated or reasoning-stripped message stopped being the same message to
    ///     anything downstream that keys on <see cref="ChatMessage.MessageId" /> or reads provider metadata off
    ///     <see cref="ChatMessage.RawRepresentation" />.
    /// </summary>
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

    /// <summary>
    ///     The excerpt marker every truncation in this repository shares. Internal rather than private because the
    ///     replayed tool history is capped at PROJECTION time (<c>ConversationContextBuilder.Build</c>), before this
    ///     budgeter ever sees the round, and a second marker shape would make two truncations of the same result read
    ///     as two different results.
    /// </summary>
    internal static string Excerpt(string value, int excerptChars, int omitted)
    {
        var marker = $"[truncated: {omitted} chars omitted]";
        return excerptChars > 0 ? $"{value[..excerptChars]}\n{marker}" : marker;
    }
}
