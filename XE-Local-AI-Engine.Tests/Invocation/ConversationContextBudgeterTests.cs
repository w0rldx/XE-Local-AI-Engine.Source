namespace XE_Local_AI_Engine.Tests.Invocation;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ConversationContextBudgeterTests
{
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
            User("u0"), Assistant("a0"),
            User("u1"), Assistant("a1"),
            User("u2"), Assistant("a2"),
            User("u3"), Assistant("a3")
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
        // Turn 0 carries a tool-call + its result; it must be dropped whole, leaving no lone result behind.
        var messages = new List<ChatMessage>
        {
            User("u0"), AssistantToolCall("call-1", "search"), ToolResult("call-1", "old result"),
            User("u1"), Assistant("a1")
        };
        var sut = CreateSut(FixedEstimator(perMessage: 10), recentTurnKeepCount: 1);

        var result = sut.Budget(messages, contextTokenCapacity: 25, reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed);
        // Turn 0 (3 messages) dropped; turn 1 (2 messages) protected.
        AssertEx.Equal(expected: 2, result.Messages.Count);
        AssertEx.Empty(ResultCallIds(result.Messages));
        AssertEx.Empty(CallCallIds(result.Messages));
        AssertNoOrphanedToolResults(result.Messages);
    }

    [Test]
    public void Budget_TruncatesOversizedHistoricalToolResult_BeforeDroppingTurns()
    {
        var bigResult = new string('x', 1000);
        var messages = new List<ChatMessage>
        {
            User("u0"), AssistantToolCall("call-1", "search"), ToolResult("call-1", bigResult),
            User("u1")
        };
        var sut = CreateSut(CharCountEstimator(), recentTurnKeepCount: 1, historicalToolResultExcerptChars: 50);

        // Budget 200 chars: truncating the 1000-char result to a ~50-char excerpt fits without dropping turn 0.
        var result = sut.Budget(messages, contextTokenCapacity: 200, reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed);
        AssertEx.Equal(expected: 0, result.MessagesDropped);
        AssertEx.Equal(expected: 1, result.ToolResultsTruncated);
        AssertEx.Equal(expected: 950, result.CharsTruncated);
        AssertEx.Equal(expected: 4, result.Messages.Count);
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
            User("u0"), Assistant("a0"),
            User("u1"), Assistant("a1"),
            // Most recent turn carries a large tool result — it belongs to the in-flight round and must be preserved.
            User("u2"), AssistantToolCall("call-9", "run"), ToolResult("call-9", protectedResult)
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
    public void Budget_WhenAlwaysKeepSetExceedsBudget_KeepsItAndFlagsOverflow()
    {
        // Two turns, keep-count 4 -> every turn is protected; nothing is droppable.
        var messages = new List<ChatMessage>
        {
            User("u0"), Assistant("a0"),
            User("u1"), Assistant("a1")
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
            User("u0"), Assistant("a0"),
            User("u1"), Assistant("a1")
        };
        var sut = CreateSut(FixedEstimator(perMessage: 10), recentTurnKeepCount: 1);

        // Force dropping the first turn; the system message shares that turn but must stay pinned.
        var result = sut.Budget(messages, contextTokenCapacity: 35, reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed);
        AssertEx.True(ContainsText(result.Messages, "system prompt"), "system messages are always kept");
        AssertEx.False(ContainsText(result.Messages, "u0"));
        AssertEx.True(ContainsText(result.Messages, "u1"));
    }

    [Test]
    public void Budget_UsesTheInjectedEstimator_NotItsOwnHeuristic()
    {
        // Tiny messages a character heuristic would never trim; the injected estimator inflates each to 1000 tokens,
        // proving the budgeter honors the abstraction rather than measuring content itself.
        var messages = new List<ChatMessage>
        {
            User("a"),
            User("b"),
            User("c")
        };
        var sut = CreateSut(FixedEstimator(perMessage: 1000), recentTurnKeepCount: 1);

        var result = sut.Budget(messages, contextTokenCapacity: 1500, reservedOutputTokens: 0);

        AssertEx.True(result.Trimmed);
        AssertEx.Equal(expected: 2, result.MessagesDropped);
        AssertEx.Equal(expected: 1, result.Messages.Count);
        AssertEx.True(ContainsText(result.Messages, "c"));
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
