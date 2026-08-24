namespace XE_Local_AI_Engine.Tests.Invocation;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ConversationContextBudgeterTests
{
    /// <summary>
    ///     The capacity whose safety-margined budget is exactly 70 tokens, for the overhead-folding test below. Derived
    ///     rather than hard-coded at 70 because the budgeter measures against
    ///     <see cref="TokenEstimatorCalibrationStore.EstimateSafetyFactor" /> of the window, so a bare 70 would leave the
    ///     control arm already trimming and the test would prove nothing about overhead.
    /// </summary>
    private static readonly int ExactlyFitsSeventy = (int)Math.Ceiling(70 / TokenEstimatorCalibrationStore.EstimateSafetyFactor);

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
        var control = sut.Budget(messages, contextTokenCapacity: ExactlyFitsSeventy, reservedOutputTokens: 0);
        AssertEx.False(control.Trimmed, "history alone fits the capacity");
        AssertEx.True(ReferenceEquals(messages, control.Messages), "an exactly-fitting history passes through unchanged");

        // A 20-char system prompt folds in 20 tokens of overhead: effective budget 50 -> the oldest turn (20) drops.
        var withPrompt = sut.Budget(messages, contextTokenCapacity: ExactlyFitsSeventy, reservedOutputTokens: 0, systemPrompt: new string('s', 20));
        AssertEx.True(withPrompt.Trimmed, "the system-prompt overhead must push the round over and force a trim");
        AssertEx.False(withPrompt.ExceedsBudget);
        AssertEx.Equal(expected: 2, withPrompt.MessagesDropped);
        AssertEx.False(ContainsText(withPrompt.Messages, "user-msg-0"), "the oldest turn drops once the prompt overhead is counted");
        AssertEx.True(ContainsText(withPrompt.Messages, "user-msg-1"), "only one turn needs to drop for the prompt-only overhead");

        // Adding a 16-char tool definition folds in 16 more tokens: effective budget 34 -> a SECOND turn must drop,
        // proving the tool-schema footprint is counted on top of the system prompt.
        var withPromptAndTool = sut.Budget(messages,
            contextTokenCapacity: ExactlyFitsSeventy,
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

    private static ConversationContextBudgeter CreateSut(ITokenEstimator estimator,
        int recentTurnKeepCount = 4,
        int historicalToolResultExcerptChars = 2000)
    {
        var options = Options.Create(new ConversationContextBudgetOptions
        {
            RecentTurnKeepCount = recentTurnKeepCount,
            HistoricalToolResultExcerptChars = historicalToolResultExcerptChars
        });
        return new ConversationContextBudgeter(estimator, options);
    }

    private static ITokenEstimator FixedEstimator(int perMessage)
    {
        return new StubTokenEstimator(_ => perMessage);
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

    private sealed class StubTokenEstimator : ITokenEstimator
    {
        private readonly Func<ChatMessage, int> _perMessage;

        public StubTokenEstimator(Func<ChatMessage, int> perMessage)
        {
            _perMessage = perMessage;
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
