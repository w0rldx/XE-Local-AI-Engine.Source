namespace XE_Local_AI_Engine.Tests.Invocation;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

public sealed class ConversationContextBudgeterTests
{
    /// <summary>
    ///     The context capacity whose safety-margined budget is <paramref name="budgetTokens" />. The budgeter measures
    ///     against <see cref="TokenEstimatorCalibrationStore.EstimateSafetyFactor" /> of the window, so a test that wants
    ///     a known budget must ask for it rather than hard-coding the capacity: a bare number leaves the arithmetic these
    ///     fixtures are built on short by the margin, and each of them then proves something other than what it was
    ///     written for. Retuning the factor moves these capacities with it.
    /// </summary>
    private static int CapacityFor(int budgetTokens) =>
        (int)Math.Ceiling(budgetTokens / TokenEstimatorCalibrationStore.EstimateSafetyFactor);

    [Test]
    public void Budget_WhenUnderBudget_ReturnsInputUnchangedByReference()
    {
        var messages = new List<ChatMessage>
        {
            User("hello"),
            Assistant("hi there")
        };
        var sut = CreateSut(CharCountEstimator());

        var result = sut.Budget(messages, contextTokenCapacity: 1000, reservedOutputTokens: 100);

        AssertEx.False(result.Trimmed);
        AssertEx.False(result.ExceedsBudget);
        AssertEx.Equal(expected: 0, result.MessagesDropped);
        AssertEx.True(ReferenceEquals(messages, result.Messages), "under-budget history must pass through unchanged");
    }

    [Test]
    public void Budget_WhenOverBudget_DropsOldestTurnsFirstWholeTurn()
    {
        // Four single-token turns (each message 10 tokens under the stub); keep only the last turn.
        var messages = new List<ChatMessage>
        {
            User("u0"),
            Assistant("a0"),
            User("u1"),
            Assistant("a1"),
            User("u2"),
            Assistant("a2"),
            User("u3"),
            Assistant("a3")
        };
        var sut = CreateSut(FixedEstimator(perMessage: 10), recentTurnKeepCount: 1);

        // Budget 45 tokens: 80 total -> drop turn0 (20) -> 60 -> drop turn1 (20) -> 40 <= 45. Turns 2 and 3 remain.
        var result = sut.Budget(messages, contextTokenCapacity: 45, reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed);
        AssertEx.Equal(expected: 4, result.MessagesDropped);
        AssertEx.Equal(expected: 4, result.Messages.Count);
        AssertEx.False(ContainsText(result.Messages, "u0"), "oldest turn must be dropped first");
        AssertEx.False(ContainsText(result.Messages, "u1"));
        AssertEx.True(ContainsText(result.Messages, "u2"));
        AssertEx.True(ContainsText(result.Messages, "a3"));
    }

    [Test]
    public void Budget_WhenDroppingTurns_NeverOrphansAToolCallFromItsResult()
    {
        // Turn 0 carries a tool-call + its result; it must be dropped whole, leaving no lone result behind. Turns 1 and
        // 2 are the protected recent window (the keep floor is 2), so the droppable region is turn 0 alone.
        var messages = new List<ChatMessage>
        {
            User("u0"),
            AssistantToolCall("call-1", "search"),
            ToolResult("call-1", "old result"),
            User("u1"),
            Assistant("a1"),
            User("u2"),
            Assistant("a2")
        };
        var sut = CreateSut(FixedEstimator(perMessage: 10), recentTurnKeepCount: 2);

        // 70 tokens total; budget 45 forces turn 0 (3 messages = 30) to drop, leaving turns 1 and 2 (40 <= 45).
        var result = sut.Budget(messages, contextTokenCapacity: 45, reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed);
        // Turn 0 (3 messages) dropped; turns 1 and 2 (4 messages) protected.
        AssertEx.Equal(expected: 4, result.Messages.Count);
        AssertEx.Empty(ResultCallIds(result.Messages));
        AssertEx.Empty(CallCallIds(result.Messages));
        AssertNoOrphanedToolResults(result.Messages);
    }

    [Test]
    public void Budget_WhenDroppingAReplayedToolExchange_DropsTheCallAndItsResultTogether()
    {
        // The same pair-integrity property, but on the messages the REPLAY actually emits rather than a hand-built
        // imitation of them: a caller-managed continuation renders its history through InvocationRunner, and the
        // budgeter's turn grouping has to treat that shape the way it treats a live tool round.
        var replayed = InvocationRunner.BuildChatMessages(RuntimePackageBuilder.Valid()
                                                                               .WithUserMessage("u0")
                                                                               .WithToolExchangeMessage("a0",
                                                                                   sortOrder: 1,
                                                                                   new ConversationToolExchange("call-1", "search", "{}", "old result", IsError: false))
                                                                               .Build());

        var messages = new List<ChatMessage>(replayed)
        {
            User("u1"),
            Assistant("a1"),
            User("u2"),
            Assistant("a2")
        };
        var sut = CreateSut(FixedEstimator(perMessage: 10), recentTurnKeepCount: 2);

        // 80 tokens total; budget 45 forces turn 0 (the user message plus the replayed call, result and answer) out.
        var result = sut.Budget(messages, contextTokenCapacity: 45, reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed);
        AssertEx.Empty(CallCallIds(result.Messages));
        AssertEx.Empty(ResultCallIds(result.Messages));
        AssertNoOrphanedToolResults(result.Messages);
    }

    [Test]
    public void Budget_TruncatesOversizedHistoricalToolResult_BeforeDroppingTurns()
    {
        var bigResult = new string('x', 1000);
        // Turn 0 holds the oversized historical result; turns 1 and 2 are the protected recent window (keep floor 2).
        var messages = new List<ChatMessage>
        {
            User("u0"),
            AssistantToolCall("call-1", "search"),
            ToolResult("call-1", bigResult),
            User("u1"),
            Assistant("a1"),
            User("u2")
        };
        var sut = CreateSut(CharCountEstimator(), recentTurnKeepCount: 2, historicalToolResultExcerptChars: 50);

        // Budget 200 chars: truncating the 1000-char result to a ~50-char excerpt fits without dropping turn 0.
        var result = sut.Budget(messages, contextTokenCapacity: 200, reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed);
        AssertEx.Equal(expected: 0, result.MessagesDropped);
        AssertEx.Equal(expected: 1, result.ToolResultsTruncated);
        AssertEx.Equal(expected: 950, result.CharsTruncated);
        AssertEx.Equal(expected: 6, result.Messages.Count);
        AssertEx.True(ContainsText(result.Messages, "u0"), "the truncated turn must NOT be dropped");
        var truncatedText = FindToolResultText(result.Messages, "call-1");
        AssertEx.Contains(truncatedText, "[truncated: 950 chars omitted]");
    }

    [Test]
    public void Budget_NeverTrimsTheCurrentRound_ProtectedRecentTurnsUntouched()
    {
        var protectedResult = new string('y', 1000);
        var messages = new List<ChatMessage>
        {
            User("u0"),
            Assistant("a0"),
            User("u1"),
            Assistant("a1"),
            // Most recent turn carries a large tool result — it belongs to the in-flight round and must be preserved.
            User("u2"),
            AssistantToolCall("call-9", "run"),
            ToolResult("call-9", protectedResult)
        };
        var sut = CreateSut(CharCountEstimator(), recentTurnKeepCount: 1, historicalToolResultExcerptChars: 50);

        var result = sut.Budget(messages, contextTokenCapacity: 100, reservedOutputTokens: 0);

        // The protected turn's oversized result is never truncated and its messages never dropped.
        AssertEx.Equal(expected: 0, result.ToolResultsTruncated);
        AssertEx.True(ContainsText(result.Messages, "u2"));
        var recentResult = FindToolResultText(result.Messages, "call-9");
        AssertEx.Equal(protectedResult, recentResult);
    }

    [Test]
    public void Budget_HonorsRecentTurnKeepCountFloor_ApprovalTwoTurnTailSurvivesWhole()
    {
        // The approval-replay path splits one in-flight round across two turns: the assistant tool-call (turn 1) and the
        // replayed User approval-decision (turn 2). A requested keep-count of 1 must be clamped up to the floor of 2 so
        // BOTH turns are protected — dropping the tool-call turn while keeping the approval decision would orphan it.
        var messages = new List<ChatMessage>
        {
            User("u0"),
            Assistant("a0"),
            User("do X"),
            AssistantToolCall("call-1", "search"),
            User("approved")
        };
        var sut = CreateSut(FixedEstimator(perMessage: 10), recentTurnKeepCount: 1);

        // 50 tokens total; budget 35 forces the oldest turn (turn 0 = 20) to drop, leaving the two-turn approval tail.
        var result = sut.Budget(messages, contextTokenCapacity: 35, reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed);
        AssertEx.False(ContainsText(result.Messages, "u0"), "the oldest droppable turn is trimmed");
        AssertEx.True(ContainsText(result.Messages, "do X"), "the tool-call turn is protected by the keep-count floor of 2");
        AssertEx.True(ContainsText(result.Messages, "approved"), "the approval-decision turn stays with its tool-call");
        AssertEx.Contains(CallCallIds(result.Messages), "call-1", "the tool-call must survive alongside its approval turn");
    }

    [Test]
    public void Budget_WhenManyApprovalRoundsPushOverBudget_KeepsEveryApprovalPairAndItsResult()
    {
        // One long tool-using turn with four approval rounds. Each replayed decision is its own ChatRole.User message,
        // so the rounds occupy turns 2..5 and the keep-count floor protects only the last two — the earlier rounds are
        // squarely inside the droppable region. Dropping turn 2 whole would take call-1's REQUEST while its response in
        // turn 3 survived, which is the orphan the approval validator fails the whole invocation on.
        var (request1, response1) = ApprovalRound("call-1", "search");
        var (request2, response2) = ApprovalRound("call-2", "search");
        var (request3, response3) = ApprovalRound("call-3", "search");
        var (request4, _) = ApprovalRound("call-4", "search");
        var messages = new List<ChatMessage>
        {
            User("u0"),
            Assistant("a0"),
            User("u1"),
            Assistant("a1"),
            User("run the tools"),
            request1,
            response1,
            ToolResult("call-1", "r1"),
            request2,
            response2,
            ToolResult("call-2", "r2"),
            request3,
            response3,
            ToolResult("call-3", "r3"),
            // The in-flight round: its request is surfaced but no decision has been replayed for it yet.
            request4
        };
        var sut = CreateSut(FixedEstimator(perMessage: 10), recentTurnKeepCount: 2);

        // 150 tokens; a budget of 100 drops the two ordinary turns (40) plus the lone unpinned message of turn 2 (10).
        var result = sut.Budget(messages, contextTokenCapacity: CapacityFor(100), reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed);
        AssertEx.False(result.ExceedsBudget);
        AssertEx.Equal(expected: 5, result.MessagesDropped);
        AssertEx.False(ContainsText(result.Messages, "u0"), "ordinary history must still be reclaimed");
        AssertEx.False(ContainsText(result.Messages, "u1"));
        AssertEx.False(ContainsText(result.Messages, "run the tools"), "the unpinned message of a partly pinned turn still drops");
        AssertNoOrphanedApprovals(result.Messages);
        // A resolved round is only replayable while the result its decision produced is also in the batch.
        AssertEx.Contains(ResultCallIds(result.Messages), "call-1", "a resolved approval keeps the result it produced");
        AssertEx.Contains(ResultCallIds(result.Messages), "call-2");
        AssertEx.Contains(ResultCallIds(result.Messages), "call-3");
    }

    [Test]
    public void Budget_WhenPinnedApprovalsAloneExceedBudget_EvictsTheOldestGroupsWhole()
    {
        // Pinning is a correlation guarantee, not a reservation. Left permanent, an approval-heavy conversation ends up
        // with a pinned set that alone exceeds the budget, ExceedsBudget stays true, and the runner's hard stop rejects
        // every later turn forever. The oldest COMPLETE rounds must therefore go — atomically, so no response is left
        // without its request and no resolved response without the result it produced.
        var (request1, response1) = ApprovalRound("call-1", "search");
        var (request2, response2) = ApprovalRound("call-2", "search");
        var (request3, response3) = ApprovalRound("call-3", "search");
        var (request4, _) = ApprovalRound("call-4", "search");
        var messages = new List<ChatMessage>
        {
            User("run the tools"),
            request1,
            response1,
            ToolResult("call-1", "r1"),
            request2,
            response2,
            ToolResult("call-2", "r2"),
            request3,
            response3,
            ToolResult("call-3", "r3"),
            // The in-flight round: surfaced, no decision replayed yet, so it is not historical and must survive.
            request4
        };
        var sut = CreateSut(FixedEstimator(perMessage: 10), recentTurnKeepCount: 2);

        // 110 tokens, of which 100 are pinned approval content. A budget of 75 is unreachable by dropping ordinary
        // history alone — only evicting whole approval groups can clear it. Any tighter and round 2 would have to go
        // too, except that it is anchored in a protected turn, so the budgeter would legitimately keep it and report
        // ExceedsBudget: the scenario would stop being about eviction at all.
        var result = sut.Budget(messages, contextTokenCapacity: CapacityFor(75), reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed);
        AssertEx.False(result.ExceedsBudget, "an approval-heavy history must not be permanently over budget");
        AssertNoOrphanedApprovals(result.Messages);

        // Oldest first: round 1 goes whole (request + decision + result), round 3 and the in-flight request 4 stay.
        var survivingRequests = ApprovalRequestIds(result.Messages);
        AssertEx.False(survivingRequests.Contains("call-1"), "the oldest complete round is evicted");
        AssertEx.False(ApprovalResponseIds(result.Messages).Contains("call-1"), "its decision goes with it, never separately");
        AssertEx.False(ResultCallIds(result.Messages).Contains("call-1"), "and so does the result that decision produced");
        AssertEx.Contains(survivingRequests, "call-3", "a newer round is kept while an older one can pay the bill");
        AssertEx.Contains(survivingRequests, "call-4", "an undecided round is not historical and is never evicted");
    }

    [Test]
    public void Budget_WhenApprovalsCarryNoCallId_StillEvictsRequestAndResponseTogether()
    {
        // A blank CallId is a supported shape (InvocationRunnerTests exercises the blank-call-id approval path).
        // Correlating a pair on CallId therefore left the request and its decision in SEPARATE groups, so eviction
        // could take one and keep the other. RequestId is the link the approval validator itself matches on and is
        // never blank, so the pair now moves as one in both directions.
        var (request1, response1) = BlankCallIdApprovalRound("approval-1");
        var (request2, response2) = BlankCallIdApprovalRound("approval-2");
        var messages = new List<ChatMessage>
        {
            User("run the tools"),
            request1,
            response1,
            request2,
            response2,
            User("u2"),
            User("u3")
        };
        var sut = CreateSut(FixedEstimator(perMessage: 10), recentTurnKeepCount: 2);

        // 70 tokens; a budget of 35 cannot be met without taking an approval group whole.
        var result = sut.Budget(messages, contextTokenCapacity: 35, reservedOutputTokens: 0);

        AssertNoOrphanedApprovals(result.Messages);
        var requests = ApprovalRequestIds(result.Messages);
        var responses = ApprovalResponseIds(result.Messages);
        AssertEx.Equal(requests.Contains("approval-1"), responses.Contains("approval-1"),
            "a blank-call-id round must be evicted whole or kept whole, never split");
        AssertEx.Equal(requests.Contains("approval-2"), responses.Contains("approval-2"));
        AssertEx.False(requests.Contains("approval-1"), "the oldest complete round is still the one that pays");
    }

    [Test]
    public void Budget_WhenTheOnlyEvictableApprovalsAreUndecided_KeepsThemAndFlagsOverflow()
    {
        // Eviction is bounded by correlation, not by the budget. These two rounds sit squarely in the droppable region
        // and the budget cannot be met without them — but neither has a replayed decision, so evicting one would delete
        // an approval the user has not answered yet. Overflow is the correct answer here.
        var (request1, _) = ApprovalRound("call-1", "search");
        var (request2, _) = ApprovalRound("call-2", "search");
        var messages = new List<ChatMessage>
        {
            User("u0"),
            request1,
            User("u1"),
            request2,
            User("u2"),
            User("u3")
        };
        var sut = CreateSut(FixedEstimator(perMessage: 10), recentTurnKeepCount: 2);

        // 60 tokens; turns 2 and 3 are protected, so the budget of 25 is unreachable once the two ordinary droppable
        // messages (20) are gone and only the undecided approval requests are left to take.
        var result = sut.Budget(messages, contextTokenCapacity: 25, reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed);
        AssertEx.True(result.ExceedsBudget, "an irreducible pinned set must still be flagged rather than broken up");
        AssertEx.Contains(ApprovalRequestIds(result.Messages), "call-1");
        AssertEx.Contains(ApprovalRequestIds(result.Messages), "call-2");
    }

    [Test]
    public void Budget_ExcerptsAnApprovedToolResult_RatherThanPinningItWholesale()
    {
        // Pinning approval rounds must not cost the budget its only lever over a long approved-tool turn: the paired
        // results stay excerptable, and excerpting preserves the call id the approval validator matches on.
        var bigResult = new string('x', 1000);
        var (request1, response1) = ApprovalRound("call-1", "search");
        var (request2, response2) = ApprovalRound("call-2", "search");
        var (request3, response3) = ApprovalRound("call-3", "search");
        var (request4, _) = ApprovalRound("call-4", "search");
        var messages = new List<ChatMessage>
        {
            User("u0"),
            Assistant("a0"),
            User("start-tools"),
            request1,
            // The oversized result belongs to round 1, two rounds back — outside the protected recent window and so
            // inside the region Pass 1 excerpts.
            response1,
            ToolResult("call-1", bigResult),
            request2,
            response2,
            ToolResult("call-2", "ok"),
            request3,
            response3,
            ToolResult("call-3", "ok3"),
            request4
        };
        var sut = CreateSut(CharCountEstimator(), recentTurnKeepCount: 2, historicalToolResultExcerptChars: 50);

        // 1020 chars; budget 200 is met by excerpting the oversized result alone, so nothing has to drop.
        var result = sut.Budget(messages, contextTokenCapacity: 200, reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed);
        AssertEx.Equal(expected: 0, result.MessagesDropped);
        AssertEx.Equal(expected: 1, result.ToolResultsTruncated);
        AssertEx.Equal(expected: 950, result.CharsTruncated);
        AssertEx.Contains(FindToolResultText(result.Messages, "call-1"), "[truncated: 950 chars omitted]");
        AssertEx.Contains(ResultCallIds(result.Messages), "call-1", "excerpting must preserve the call id");
        AssertNoOrphanedApprovals(result.Messages);
    }

    [Test]
    public void Budget_WhenAlwaysKeepSetExceedsBudget_KeepsItAndFlagsOverflow()
    {
        // Two turns, keep-count 4 -> every turn is protected; nothing is droppable.
        var messages = new List<ChatMessage>
        {
            User("u0"),
            Assistant("a0"),
            User("u1"),
            Assistant("a1")
        };
        var sut = CreateSut(FixedEstimator(perMessage: 100), recentTurnKeepCount: 4);

        var result = sut.Budget(messages, contextTokenCapacity: 50, reservedOutputTokens: 0);

        AssertEx.False(result.Trimmed);
        AssertEx.True(result.ExceedsBudget, "an unavoidable overrun must be flagged for logging");
        AssertEx.Equal(expected: 0, result.MessagesDropped);
        AssertEx.Equal(expected: 4, result.Messages.Count);
    }

    [Test]
    public void Budget_AlwaysKeepsSystemMessages_EvenInsideADroppedTurn()
    {
        var messages = new List<ChatMessage>
        {
            System("system prompt"),
            User("u0"),
            Assistant("a0"),
            User("u1"),
            Assistant("a1"),
            User("u2"),
            Assistant("a2")
        };
        var sut = CreateSut(FixedEstimator(perMessage: 10), recentTurnKeepCount: 2);

        // Force dropping the first turn (turns 1 and 2 are the protected window); the system message shares that turn
        // but must stay pinned. 70 total; budget 55 drops turn 0's user/assistant (20) leaving the pinned system (50).
        var result = sut.Budget(messages, contextTokenCapacity: 55, reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed);
        AssertEx.True(ContainsText(result.Messages, "system prompt"), "system messages are always kept");
        AssertEx.False(ContainsText(result.Messages, "u0"));
        AssertEx.True(ContainsText(result.Messages, "u1"));
    }

    [Test]
    public void Budget_UsesTheInjectedEstimator_NotItsOwnHeuristic()
    {
        // Tiny messages a character heuristic would never trim, but the injected estimator inflates each to 1000
        // tokens, proving the budgeter honors the abstraction rather than measuring content itself. Of the four
        // single-message turns the last two form the protected recent window, so only the two oldest turns can drop.
        var messages = new List<ChatMessage>
        {
            User("a"),
            User("b"),
            User("c"),
            User("d")
        };
        var sut = CreateSut(FixedEstimator(perMessage: 1000), recentTurnKeepCount: 2);

        var result = sut.Budget(messages, contextTokenCapacity: 2500, reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed);
        AssertEx.Equal(expected: 2, result.MessagesDropped);
        AssertEx.Equal(expected: 2, result.Messages.Count);
        AssertEx.True(ContainsText(result.Messages, "c"));
    }

    [Test]
    public void Budget_FoldsSystemPromptAndToolOverheadIntoCapacity_TrimsWhatHistoryAloneWouldNot()
    {
        // ORC-02: the system prompt is prepended AFTER this history and tool schemas never appear in the message list,
        // yet both count against the launched window. A history that fits when measured alone must now trim once the
        // fixed overhead is folded in — otherwise the outer budget passes an actually-over-window round through.
        // Four 10-char turns (70 total); keep-count 2 protects turns 2 and 3, leaving turns 0 and 1 (40) droppable.
        var messages = new List<ChatMessage>
        {
            User("user-msg-0"),
            Assistant("asst-msg-0"),
            User("user-msg-1"),
            Assistant("asst-msg-1"),
            User("user-msg-2"),
            Assistant("asst-msg-2"),
            User("user-msg-3")
        };
        var sut = CreateSut(CharCountEstimator(), recentTurnKeepCount: 2);

        // Control: history (70) fits a 70-token capacity with no overhead, so nothing is trimmed.
        var control = sut.Budget(messages, contextTokenCapacity: CapacityFor(70), reservedOutputTokens: 0);
        AssertEx.False(control.Trimmed, "history alone fits the capacity");
        AssertEx.True(ReferenceEquals(messages, control.Messages), "an exactly-fitting history passes through unchanged");

        // A 20-char system prompt folds in 20 tokens of overhead: effective budget 50 -> the oldest turn (20) drops.
        var withPrompt = sut.Budget(messages, contextTokenCapacity: CapacityFor(70), reservedOutputTokens: 0, systemPrompt: new string('s', 20));
        AssertEx.True(withPrompt.Trimmed, "the system-prompt overhead must push the round over and force a trim");
        AssertEx.False(withPrompt.ExceedsBudget);
        AssertEx.Equal(expected: 2, withPrompt.MessagesDropped);
        AssertEx.False(ContainsText(withPrompt.Messages, "user-msg-0"), "the oldest turn drops once the prompt overhead is counted");
        AssertEx.True(ContainsText(withPrompt.Messages, "user-msg-1"), "only one turn needs to drop for the prompt-only overhead");

        // Adding a 16-char tool definition folds in 16 more tokens: effective budget 34 -> a SECOND turn must drop,
        // proving the tool-schema footprint is counted on top of the system prompt.
        var withPromptAndTool = sut.Budget(messages,
            contextTokenCapacity: CapacityFor(70),
            reservedOutputTokens: 0,
            systemPrompt: new string('s', 20),
            toolDefinitions: [new string('t', 16)]);
        AssertEx.True(withPromptAndTool.Trimmed);
        AssertEx.False(withPromptAndTool.ExceedsBudget);
        AssertEx.Equal(expected: 4, withPromptAndTool.MessagesDropped);
        AssertEx.False(ContainsText(withPromptAndTool.Messages, "user-msg-1"), "the tool-schema overhead forces a second turn to drop");
        AssertEx.True(ContainsText(withPromptAndTool.Messages, "user-msg-2"), "the protected recent turns are still kept");
    }

    [Test]
    public void Budget_WhenTheEstimateSitsJustUnderTheWindow_StillTrims()
    {
        // The safety margin's whole point: an estimate at 0.9x the window used to pass as "fitting", and because the
        // char heuristic under-counts by roughly a tenth, the provider then rejected the round outright rather than the
        // budgeter trimming it. Budgeting against 85% turns that back into a trim.
        var messages = new List<ChatMessage>
        {
            User("turn one"),
            Assistant("answer one"),
            User("turn two"),
            Assistant("answer two"),
            User("turn three"),
            Assistant("answer three")
        };
        var sut = CreateSut(FixedEstimator(perMessage: 15), recentTurnKeepCount: 2);

        // Six messages at 15 = 90 estimated, against a window of 100 with nothing reserved: 90% of the window.
        var result = sut.Budget(messages, contextTokenCapacity: 100, reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed, "An estimate at 0.9x the window is inside the margin and must be trimmed.");
        AssertEx.True(result.MessagesDropped > 0);
        AssertEx.True(result.EstimatedTokensAfter <= TokenEstimatorCalibrationStore.ApplySafetyMargin(100),
            $"The trimmed round must fit the margin, was {result.EstimatedTokensAfter}.");
    }

    [Test]
    public void Budget_WhenProtectedReasoningIsTheOverflow_StripsItOldestFirstAndStopsOnceItFits()
    {
        // The failure Pass 4 exists for: every turn is inside the protected window, so Passes 1-3 have nothing to take
        // and the round would hard-fail. Stripping the OLDEST message's reasoning alone closes the gap, so the newer
        // message's reasoning must survive untouched — a pass that stripped unconditionally, or newest-first, would
        // needlessly discard the thinking closest to the in-flight round.
        var messages = new List<ChatMessage>
        {
            User("u0"),
            AssistantReasoning(new string('r', 100), "a0"),
            AssistantReasoning(new string('s', 100), "a1"),
            User("u1")
        };
        var sut = CreateSut(CharCountEstimator(), stripProtectedReasoning: true);

        // 208 chars; a budget of 120 is met by stripping the first 100-char reasoning alone (208 -> 108).
        var result = sut.Budget(messages, contextTokenCapacity: CapacityFor(120), reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed);
        AssertEx.False(result.ExceedsBudget, "stripping superseded reasoning must rescue a round that would otherwise fail");
        AssertEx.Equal(expected: 1, result.ReasoningStrippedCount);
        AssertEx.Equal(expected: 0, result.MessagesDropped);
        AssertEx.Equal(expected: 0, result.ProtectedResultsExcerptedCount);
        AssertEx.Equal(expected: 108, result.EstimatedTokensAfter);
        AssertEx.Equal(string.Empty, ReasoningTextOf(result.Messages[1]), "the oldest reasoning is the one that pays");
        AssertEx.Equal(new string('s', 100), ReasoningTextOf(result.Messages[2]), "stripping stops the moment the round fits");
        AssertEx.True(ContainsText(result.Messages, "a0"), "only the reasoning part is removed, never the message's answer text");
    }

    [Test]
    public void Budget_WhenStrippingProtectedReasoningIsDisabled_KeepsItAndFlagsOverflow()
    {
        // The pass is opt-in, so with it off this is exactly today's behaviour: the protected window is irreducible, the
        // overflow is flagged, and the runner's hard stop fails the turn.
        var messages = new List<ChatMessage>
        {
            User("u0"),
            AssistantReasoning(new string('r', 100), "a0"),
            AssistantReasoning(new string('s', 100), "a1"),
            User("u1")
        };
        var sut = CreateSut(CharCountEstimator(), stripProtectedReasoning: false);

        var result = sut.Budget(messages, contextTokenCapacity: CapacityFor(120), reservedOutputTokens: 0);

        AssertEx.False(result.Trimmed);
        AssertEx.True(result.ExceedsBudget);
        AssertEx.Equal(expected: 0, result.ReasoningStrippedCount);
        AssertEx.Equal(new string('r', 100), ReasoningTextOf(result.Messages[1]));
        AssertEx.Equal(new string('s', 100), ReasoningTextOf(result.Messages[2]));
    }

    [Test]
    public void Budget_Pass4_NeverStripsTheLastMessage_EvenWhenStillOverBudget()
    {
        // The last message is the in-flight round the model is producing against. Reclaiming from it is the one thing
        // that could leave the next provider call without the content it is answering, so overflow is the right answer.
        var messages = new List<ChatMessage>
        {
            User("u0"),
            AssistantReasoning(new string('r', 100), "a0"),
            AssistantReasoning(new string('t', 200))
        };
        var sut = CreateSut(CharCountEstimator(), stripProtectedReasoning: true);

        // 304 chars; a budget of 150 is unreachable once the last message is off limits (304 - 100 = 204).
        var result = sut.Budget(messages, contextTokenCapacity: CapacityFor(150), reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed);
        AssertEx.True(result.ExceedsBudget, "an irreducible last message must still be flagged rather than reclaimed");
        AssertEx.Equal(expected: 1, result.ReasoningStrippedCount);
        AssertEx.Equal(new string('t', 200), ReasoningTextOf(result.Messages[2]), "the in-flight round's own reasoning is never taken");
    }

    [Test]
    public void Budget_Pass4_DropsAReasoningOnlyMessageWhole_RatherThanSendingItEmpty()
    {
        var messages = new List<ChatMessage>
        {
            User("u0"),
            AssistantReasoning(new string('r', 100)),
            Assistant("a1"),
            User("u1")
        };
        var sut = CreateSut(CharCountEstimator(), stripProtectedReasoning: true);

        var result = sut.Budget(messages, contextTokenCapacity: CapacityFor(50), reservedOutputTokens: 0);

        AssertEx.False(result.ExceedsBudget);
        AssertEx.Equal(expected: 1, result.ReasoningStrippedCount);
        AssertEx.Equal(expected: 1, result.MessagesDropped, "a message whose only content was reasoning goes whole");
        AssertEx.Equal(expected: 3, result.Messages.Count);
        AssertEx.Empty(result.Messages.Where(static message => message.Contents.Count == 0), "a contentless message must never be sent");
    }

    [Test]
    public void Budget_Pass4_TouchesSurvivorsOnly_AndLeavesToolCorrelationIntact()
    {
        // Reasoning inside a turn Pass 2 already dropped is not "stripped" — it is gone, and counting it would inflate
        // the number the notice reports. And the pass must never reach a tool call, its result, or their correlation.
        var messages = new List<ChatMessage>
        {
            User("u0"),
            AssistantReasoning(new string('h', 100)),
            User("run"),
            AssistantToolCall("call-1", "search"),
            ToolResult("call-1", "r1"),
            AssistantReasoning(new string('r', 100), "a1"),
            User("u2")
        };
        var sut = CreateSut(CharCountEstimator(), recentTurnKeepCount: 2, stripProtectedReasoning: true);

        // 217 chars. Budget 110: Pass 2 drops turn 0 (102) leaving 115, still over; Pass 4 then strips the ONE surviving
        // reasoning (100) leaving 15.
        var result = sut.Budget(messages, contextTokenCapacity: CapacityFor(110), reservedOutputTokens: 0);

        AssertEx.False(result.ExceedsBudget);
        AssertEx.Equal(expected: 2, result.MessagesDropped, "the oldest turn is still reclaimed the ordinary way first");
        AssertEx.Equal(expected: 1, result.ReasoningStrippedCount, "only reasoning on a SURVIVING message is stripped or counted");
        AssertEx.Contains(CallCallIds(result.Messages), "call-1", "the tool call must survive Pass 4 untouched");
        AssertEx.Contains(ResultCallIds(result.Messages), "call-1", "and so must the result correlated to it");
        AssertEx.Equal("r1", FindToolResultText(result.Messages, "call-1"), "Pass 4 never shortens a tool result");
        AssertNoOrphanedToolResults(result.Messages);
    }

    [Test]
    public void Budget_Pass5_ExcerptsProtectedToolResultsOldestFirstAndStopsOnceItFits()
    {
        var messages = new List<ChatMessage>
        {
            User("u0"),
            ToolResult("call-1", new string('x', 300)),
            ToolResult("call-2", new string('y', 300)),
            User("u1")
        };
        var sut = CreateSut(CharCountEstimator(),
            historicalToolResultExcerptChars: 50,
            excerptProtectedToolResults: true);

        // 604 chars, every message inside the protected window. Excerpting the OLDEST result alone (300 -> 81) brings the
        // round to 385, under a budget of 400 — so the newer result must be left whole.
        var result = sut.Budget(messages, contextTokenCapacity: CapacityFor(400), reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed);
        AssertEx.False(result.ExceedsBudget);
        AssertEx.Equal(expected: 0, result.ToolResultsTruncated, "Pass 1 never touches the protected window; this is Pass 5's count");
        AssertEx.Equal(expected: 1, result.ProtectedResultsExcerptedCount);
        AssertEx.Equal(expected: 250, result.CharsTruncated);
        AssertEx.Contains(FindToolResultText(result.Messages, "call-1"), "[truncated: 250 chars omitted]");
        AssertEx.Equal(new string('y', 300), FindToolResultText(result.Messages, "call-2"), "excerpting stops the moment the round fits");
        AssertEx.Contains(ResultCallIds(result.Messages), "call-1", "excerpting must preserve the call id the validator matches on");
    }

    [Test]
    public void Budget_Pass5_NeverExcerptsTheLastMessage_EvenWhenStillOverBudget()
    {
        // The last message is the pending tool result the model must answer next; shortening it is the one reclaim that
        // could change what the round is about.
        var messages = new List<ChatMessage>
        {
            User("u0"),
            ToolResult("call-1", new string('x', 300))
        };
        var sut = CreateSut(CharCountEstimator(),
            historicalToolResultExcerptChars: 50,
            excerptProtectedToolResults: true);

        var result = sut.Budget(messages, contextTokenCapacity: CapacityFor(100), reservedOutputTokens: 0);

        AssertEx.True(result.ExceedsBudget);
        AssertEx.Equal(expected: 0, result.ProtectedResultsExcerptedCount);
        AssertEx.Equal(new string('x', 300), FindToolResultText(result.Messages, "call-1"));
    }

    [Test]
    public void Budget_WhenExcerptingProtectedToolResultsIsDisabled_ReproducesTodaysOverflow()
    {
        var messages = new List<ChatMessage>
        {
            User("u0"),
            ToolResult("call-1", new string('x', 300)),
            ToolResult("call-2", new string('y', 300)),
            User("u1")
        };
        var sut = CreateSut(CharCountEstimator(), historicalToolResultExcerptChars: 50, stripProtectedReasoning: true);

        var result = sut.Budget(messages, contextTokenCapacity: CapacityFor(400), reservedOutputTokens: 0);

        AssertEx.False(result.Trimmed);
        AssertEx.True(result.ExceedsBudget, "Pass 5 stays opt-in: with it off the protected window is irreducible");
        AssertEx.Equal(expected: 0, result.ProtectedResultsExcerptedCount);
        AssertEx.Equal(new string('x', 300), FindToolResultText(result.Messages, "call-1"));
    }

    [Test]
    public void Budget_WhenTheOrdinaryPassesAlreadyFit_NeverFiresPass4Or5()
    {
        // Both last-resort passes are enabled here and must still report zero: they exist for the rounds that would
        // otherwise hard-fail, not as a routine reclaim on top of a trim that already worked.
        var messages = new List<ChatMessage>
        {
            User("u0"),
            AssistantReasoning(new string('h', 100), "a0"),
            User("u1"),
            AssistantReasoning(new string('r', 100), "a1"),
            User("u2"),
            ToolResult("call-1", new string('x', 100)),
            User("u3")
        };
        var sut = CreateSut(CharCountEstimator(),
            recentTurnKeepCount: 2,
            historicalToolResultExcerptChars: 50,
            stripProtectedReasoning: true,
            excerptProtectedToolResults: true);

        // 312 chars; a budget of 250 is met by dropping turn 0 (104) alone.
        var result = sut.Budget(messages, contextTokenCapacity: CapacityFor(250), reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed);
        AssertEx.False(result.ExceedsBudget);
        AssertEx.Equal(expected: 0, result.ReasoningStrippedCount);
        AssertEx.Equal(expected: 0, result.ProtectedResultsExcerptedCount);
        AssertEx.Equal(new string('r', 100), ReasoningTextOf(result.Messages[1]), "surviving reasoning is untouched when the ordinary passes suffice");
        AssertEx.Equal(new string('x', 100), FindToolResultText(result.Messages, "call-1"));
    }

    [Test]
    public void Budget_EveryRewrite_PreservesMessageIdentity()
    {
        // Every rewrite used to reconstruct a message from role + contents, silently dropping the id, author name,
        // provider raw representation and additional properties. All three rewriting passes are covered here — Pass 1's
        // historical excerpt, Pass 4's reasoning strip, Pass 5's protected excerpt.
        var raw = new object();
        // The historical result is pinned to an UNDECIDED approval round, so Pass 1 excerpts it but Pass 2/3 leave it in
        // place — otherwise the message this test wants to inspect would simply be dropped before it could be inspected.
        var (undecidedRequest, _) = ApprovalRound("call-0", "search");
        var historical = Identified(ToolResult("call-0", new string('h', 300)), "message-historical", raw);
        var reasoning = Identified(AssistantReasoning(new string('r', 300), "a1"), "message-reasoning", raw);
        var protectedResult = Identified(ToolResult("call-1", new string('x', 300)), "message-protected", raw);
        var messages = new List<ChatMessage>
        {
            User("u0"),
            undecidedRequest,
            historical,
            User("u1"),
            reasoning,
            protectedResult,
            User("u2")
        };
        var sut = CreateSut(CharCountEstimator(),
            recentTurnKeepCount: 2,
            historicalToolResultExcerptChars: 50,
            stripProtectedReasoning: true,
            excerptProtectedToolResults: true);

        // 908 chars against a budget of 120 forces all three rewriting passes to run before the round fits.
        var result = sut.Budget(messages, contextTokenCapacity: CapacityFor(120), reservedOutputTokens: 0);

        AssertEx.Equal(expected: 1, result.ToolResultsTruncated);
        AssertEx.Equal(expected: 1, result.ReasoningStrippedCount);
        AssertEx.Equal(expected: 1, result.ProtectedResultsExcerptedCount);
        foreach (var messageId in new[]
                 {
                     "message-historical",
                     "message-reasoning",
                     "message-protected"
                 })
        {
            var rewritten = AssertEx.NotNull(result.Messages.FirstOrDefault(message => string.Equals(message.MessageId, messageId, StringComparison.Ordinal)));
            AssertEx.Equal("budgeter-identity", rewritten.AuthorName, "a rewrite must keep the message's author");
            AssertEx.True(ReferenceEquals(raw, rewritten.RawRepresentation), "a rewrite must keep the provider raw representation");
            AssertEx.Equal("kept", AssertEx.NotNull(rewritten.AdditionalProperties)["marker"] as string, "a rewrite must keep the additional properties");
        }
    }

    [Test]
    public void Budget_ReportsEstimatedTokensAfter_MeasuredOnTheMaterializedSurvivors()
    {
        // The running total is maintained incrementally across five passes that add, subtract and rewrite; the reported
        // figure — and the ExceedsBudget hard stop derived from it — must describe the list actually being sent.
        var estimator = CharCountEstimator();
        var messages = new List<ChatMessage>
        {
            User("u0"),
            AssistantReasoning(new string('h', 200)),
            User("u1"),
            ToolResult("call-1", new string('x', 300)),
            AssistantReasoning(new string('r', 200), "a1"),
            User("u2")
        };
        var sut = CreateSut(estimator,
            recentTurnKeepCount: 2,
            historicalToolResultExcerptChars: 50,
            stripProtectedReasoning: true,
            excerptProtectedToolResults: true);

        var result = sut.Budget(messages, contextTokenCapacity: CapacityFor(150), reservedOutputTokens: 0);

        AssertEx.Equal(estimator.EstimateTokens(result.Messages), result.EstimatedTokensAfter);
    }

    private static ChatMessage Identified(ChatMessage message, string messageId, object rawRepresentation)
    {
        message.MessageId = messageId;
        message.AuthorName = "budgeter-identity";
        message.RawRepresentation = rawRepresentation;
        message.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            ["marker"] = "kept"
        };
        return message;
    }

    // The last two defaults track the SHIPPED defaults deliberately, so a test that says nothing about them is a test
    // about production behaviour. A test that wants a pass off says so explicitly.
    [Test]
    public void Budget_WithNoObservedCorrection_MeasuresAgainstTheSafetyMarginAlone()
    {
        // The control for the pair below. Six ten-token turns (120 tokens) against a margined budget of 85: the two
        // oldest turns go and the round fits at 80. At a neutral correction the effective budget is exactly the
        // margined window, so every fixture written before the observed-ratio channel existed still proves what it was
        // written for.
        var sut = CreateSut(FixedEstimator(perMessage: 10), recentTurnKeepCount: 1);

        var result = sut.Budget(SixTurns(), CapacityFor(85), reservedOutputTokens: 0, modelName: CalibratedModel);

        AssertEx.Equal(expected: 4, result.MessagesDropped);
    }

    [Test]
    public void Budget_WhenTheModelIsObservedToCostMoreThanEstimated_TrimsEarlier()
    {
        // Same history, same window, same estimator arithmetic — the ONLY difference is that real rounds of this model
        // have been observed to cost half again what the char heuristic predicts. The budget the estimate is measured
        // against shrinks (85 -> 56), so two further turns go rather than the round reaching the provider and coming
        // back as the context-size 400 the budgeter believed could not happen.
        var sut = CreateSut(FixedEstimator(perMessage: 10, observedCorrection: 1.5), recentTurnKeepCount: 1);

        var result = sut.Budget(SixTurns(), CapacityFor(85), reservedOutputTokens: 0, modelName: CalibratedModel);

        AssertEx.True(result.Trimmed);
        AssertEx.Equal(expected: 8, result.MessagesDropped);
    }

    [Test]
    public void Budget_WhenTheModelIsObservedToCostLessThanEstimated_DoesNotWidenTheWindow()
    {
        // Tighten-only: a below-neutral observation is remembered but never spent, because widening past the safety
        // factor on the strength of a heuristic already known to run optimistic is the failure this whole mechanism
        // exists to avoid.
        var sut = CreateSut(FixedEstimator(perMessage: 10, observedCorrection: 0.6), recentTurnKeepCount: 1);

        var result = sut.Budget(SixTurns(), CapacityFor(85), reservedOutputTokens: 0, modelName: CalibratedModel);

        AssertEx.Equal(expected: 4, result.MessagesDropped);
    }

    private const string CalibratedModel = "qwen3.8-27b:Q4_K_M";

    /// <summary>Six single-message-pair turns of ten estimated tokens each; the keep-window floor of two protects the last two.</summary>
    private static List<ChatMessage> SixTurns()
    {
        var messages = new List<ChatMessage>(12);
        for (var turn = 0; turn < 6; turn++)
        {
            messages.Add(User($"u{turn}"));
            messages.Add(Assistant($"a{turn}"));
        }

        return messages;
    }

    private static ConversationContextBudgeter CreateSut(ITokenEstimator estimator,
        int recentTurnKeepCount = 4,
        int historicalToolResultExcerptChars = 2000,
        bool stripProtectedReasoning = true,
        bool excerptProtectedToolResults = false)
    {
        var options = Options.Create(new ConversationContextBudgetOptions
        {
            RecentTurnKeepCount = recentTurnKeepCount,
            HistoricalToolResultExcerptChars = historicalToolResultExcerptChars,
            StripProtectedReasoning = stripProtectedReasoning,
            ExcerptProtectedToolResults = excerptProtectedToolResults
        });
        return new ConversationContextBudgeter(estimator, options);
    }

    private static ITokenEstimator FixedEstimator(int perMessage,
        double observedCorrection = TokenEstimatorCalibrationStore.NeutralObservedCorrection)
    {
        return new StubTokenEstimator(_ => perMessage, observedCorrection);
    }

    private static ITokenEstimator CharCountEstimator()
    {
        return new StubTokenEstimator(CharCount);
    }

    private static int CharCount(ChatMessage message)
    {
        var total = 0;
        foreach (var content in message.Contents)
        {
            total += content switch
            {
                TextContent text => text.Text?.Length ?? 0,
                TextReasoningContent reasoning => reasoning.Text?.Length ?? 0,
                FunctionCallContent call => call.Name.Length,
                FunctionResultContent result => result.Result?.ToString()?.Length ?? 0,
                _ => 0
            };
        }

        return total;
    }

    private static ChatMessage AssistantReasoning(string reasoning, string? text = null)
    {
        var contents = new List<AIContent>
        {
            new TextReasoningContent(reasoning)
        };
        if (text is not null)
        {
            contents.Add(new TextContent(text));
        }

        return new ChatMessage(ChatRole.Assistant, contents);
    }

    private static string ReasoningTextOf(ChatMessage message)
    {
        return string.Concat(message.Contents.OfType<TextReasoningContent>().Select(static reasoning => reasoning.Text ?? string.Empty));
    }

    private static ChatMessage User(string text)
    {
        return new ChatMessage(ChatRole.User, [new TextContent(text)]);
    }

    private static ChatMessage Assistant(string text)
    {
        return new ChatMessage(ChatRole.Assistant, [new TextContent(text)]);
    }

    private static ChatMessage System(string text)
    {
        return new ChatMessage(ChatRole.System, [new TextContent(text)]);
    }

    private static ChatMessage AssistantToolCall(string callId, string name)
    {
        return new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, name, new Dictionary<string, object?>())]);
    }

    // One resolved approval round as the runner replays it: the assistant segment carrying the request, then the
    // separate user message carrying the decision FunctionInvokingChatClient correlates back to it by request id.
    private static (ChatMessage Request, ChatMessage Response) ApprovalRound(string callId, string toolName)
    {
        var request = new ToolApprovalRequestContent(callId, new FunctionCallContent(callId, toolName));
        return (new ChatMessage(ChatRole.Assistant, [request]),
            new ChatMessage(ChatRole.User, [request.CreateResponse(approved: true, "Approved by user.")]));
    }

    /// <summary>An approval round for a tool call that carries NO call id — only the request id links the pair.</summary>
    private static (ChatMessage Request, ChatMessage Response) BlankCallIdApprovalRound(string requestId)
    {
        var request = new ToolApprovalRequestContent(requestId, new FunctionCallContent(string.Empty, "run_in_agent_home"));
        return (new ChatMessage(ChatRole.Assistant, [request]),
            new ChatMessage(ChatRole.User, [request.CreateResponse(approved: true, "Approved by user.")]));
    }

    private static ChatMessage ToolResult(string callId, string result)
    {
        return new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, result)]);
    }

    private static bool ContainsText(IReadOnlyList<ChatMessage> messages, string needle)
    {
        return messages.Any(message => message.Contents.OfType<TextContent>().Any(text => (text.Text ?? string.Empty).Contains(needle, StringComparison.Ordinal)));
    }

    private static IReadOnlyList<string> ResultCallIds(IReadOnlyList<ChatMessage> messages)
    {
        return [.. messages.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Select(r => r.CallId)];
    }

    private static IReadOnlyList<string> ApprovalRequestIds(IReadOnlyList<ChatMessage> messages)
    {
        return [.. messages.SelectMany(m => m.Contents.OfType<ToolApprovalRequestContent>()).Select(r => r.RequestId)];
    }

    private static IReadOnlyList<string> ApprovalResponseIds(IReadOnlyList<ChatMessage> messages)
    {
        return [.. messages.SelectMany(m => m.Contents.OfType<ToolApprovalResponseContent>()).Select(r => r.RequestId)];
    }

    private static IReadOnlyList<string> CallCallIds(IReadOnlyList<ChatMessage> messages)
    {
        return [.. messages.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Select(c => c.CallId)];
    }

    private static string FindToolResultText(IReadOnlyList<ChatMessage> messages, string callId)
    {
        var result = messages.SelectMany(m => m.Contents.OfType<FunctionResultContent>())
                             .First(r => string.Equals(r.CallId, callId, StringComparison.Ordinal));
        return result.Result?.ToString() ?? string.Empty;
    }

    private static void AssertNoOrphanedToolResults(IReadOnlyList<ChatMessage> messages)
    {
        var callIds = CallCallIds(messages);
        foreach (var resultId in ResultCallIds(messages))
        {
            AssertEx.Contains(callIds, resultId, "a tool result must retain its originating tool call");
        }
    }

    private static void AssertNoOrphanedApprovals(IReadOnlyList<ChatMessage> messages)
    {
        var requestIds = messages.SelectMany(m => m.Contents.OfType<ToolApprovalRequestContent>()).Select(r => r.RequestId).ToList();
        foreach (var response in messages.SelectMany(m => m.Contents.OfType<ToolApprovalResponseContent>()))
        {
            AssertEx.Contains(requestIds, response.RequestId, "an approval response must retain its originating request");
        }
    }

    private sealed class StubTokenEstimator : ITokenEstimator
    {
        private readonly Func<ChatMessage, int> _perMessage;
        private readonly double _observedCorrection;

        public StubTokenEstimator(Func<ChatMessage, int> perMessage,
            double observedCorrection = TokenEstimatorCalibrationStore.NeutralObservedCorrection)
        {
            _perMessage = perMessage;
            _observedCorrection = observedCorrection;
        }

        public double ResolveObservedCorrection(string? modelName)
        {
            return _observedCorrection;
        }

        public int EstimateTokens(ChatMessage message)
        {
            return _perMessage(message);
        }

        public int EstimateTokens(IReadOnlyList<ChatMessage> messages)
        {
            var total = 0;
            foreach (var message in messages)
            {
                total += _perMessage(message);
            }

            return total;
        }
    }
}
