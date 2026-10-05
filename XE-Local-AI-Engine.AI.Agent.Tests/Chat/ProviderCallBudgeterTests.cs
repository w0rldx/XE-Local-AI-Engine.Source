namespace XE_Local_AI_Engine.AI.Agent.Tests.Chat;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class ProviderCallBudgeterTests
{
    [Test]
    public void Budget_WhenUnderWindow_ReturnsInputUnchangedByReference()
    {
        var messages = new List<ChatMessage>
        {
            User("hello"),
            Assistant("hi there")
        };

        var result = ProviderCallBudgeter.Budget(messages, instructionsTokens: 0, effectiveWindowTokens: 1000, Options());

        AssertEx.False(result.Trimmed);
        AssertEx.False(result.ExceedsWindow);
        AssertEx.True(ReferenceEquals(messages, result.Messages), "under-window input must pass through unchanged");
    }

    [Test]
    public void Budget_ExcerptsOversizedToolResult_AndNeverDropsThePendingResult()
    {
        var big = new string('x', 1000);
        var messages = new List<ChatMessage>
        {
            System("sys"),
            User("u0"),
            AssistantToolCall("c1", "search"),
            ToolResult("c1", big)
        };

        // Window small enough that the 1000-char result must be excerpted; the result is the LAST (pending) message.
        var result = ProviderCallBudgeter.Budget(messages, instructionsTokens: 0, effectiveWindowTokens: 60, Options(recentKeep: 2, excerptChars: 50));

        AssertEx.True(result.Trimmed);
        AssertEx.Equal(expected: 1, result.ToolResultsTruncated);
        AssertEx.True(ContainsText(result.Messages, "sys"), "the system message is always kept");
        var pending = FindToolResultText(result.Messages, "c1");
        AssertEx.Contains(pending, "[truncated:");
        AssertEx.Contains(pending, "chars omitted]");
    }

    [Test]
    public void Budget_DropsOldestNonProtected_ButKeepsSystemRecentAndLast()
    {
        var messages = new List<ChatMessage>
        {
            System("sys"),
            User("u0"),
            Assistant("a0"),
            User("u1"),
            Assistant("a1"),
            User("u2")
        };

        // Each short message estimates ~4 tokens (len/4 + 4); total ~24. Window 18 forces dropping the two oldest
        // droppable messages (u0, a0). Protected: system, the recent 2 (a1, u2), and the last (u2).
        var result = ProviderCallBudgeter.Budget(messages, instructionsTokens: 0, effectiveWindowTokens: 18, Options(recentKeep: 2));

        AssertEx.True(result.Trimmed);
        AssertEx.True(result.MessagesDropped >= 1);
        AssertEx.True(ContainsText(result.Messages, "sys"), "system is always kept");
        AssertEx.False(ContainsText(result.Messages, "u0"), "the oldest droppable message is trimmed first");
        AssertEx.True(ContainsText(result.Messages, "a1"), "the recent window is preserved");
        AssertEx.True(ContainsText(result.Messages, "u2"), "the last message is always kept");
    }

    /// <summary>
    ///     Model-matrix D2: three tool rounds pushed the user's message out of the recent window and it was dropped (llama-server:
    ///     "No user query found"). It is irreducible; the round is flagged over the window instead.
    /// </summary>
    [Test]
    public void Budget_NeverDropsTheUsersCurrentMessage_AndFlagsTheRoundWhenOnlyDroppingItWouldFit()
    {
        var question = $"Find the code in the attached file. {new string('x', 400)}";
        var messages = new List<ChatMessage>
        {
            System("sys"),
            User(question),
            AssistantToolCall("c1", "search_text"),
            ToolResult("c1", "No matches found."),
            AssistantToolCall("c2", "search_text"),
            ToolResult("c2", "No matches found."),
            AssistantToolCall("c3", "search_text"),
            ToolResult("c3", "No matches found.")
        };
        var total = ProviderMessageTokenEstimator.EstimateTokens(messages, 4);

        var result = ProviderCallBudgeter.Budget(messages, instructionsTokens: 0, effectiveWindowTokens: total - 50, Options());

        AssertEx.True(ContainsText(result.Messages, question), "the user's current message is kept");
        AssertEx.True(result.ExceedsWindow, "the round is over the window without dropping it, so it fails as a context-window error");
    }

    [Test]
    public void Budget_InstructionsCountTowardTheWindow()
    {
        var messages = new List<ChatMessage>
        {
            User("u0"),
            Assistant("a0"),
            User("u1")
        };

        // The messages alone fit a window of 40; a large instructions estimate pushes the round over, forcing a trim.
        var underResult = ProviderCallBudgeter.Budget(messages, instructionsTokens: 0, effectiveWindowTokens: 40, Options(recentKeep: 2));
        var overResult = ProviderCallBudgeter.Budget(messages, instructionsTokens: 100, effectiveWindowTokens: 40, Options(recentKeep: 2));

        AssertEx.False(underResult.Trimmed);
        AssertEx.True(overResult.Trimmed, "the system-prompt token cost must count against the round window");
    }

    [Test]
    public void Budget_KeepsToolCallAndResult_WhenResultIsProtected_NeverOrphans()
    {
        // The tool CALL is in the droppable range but its RESULT sits in the protected recent-keep window. Dropping the
        // call alone (as the old index-wise trim did) would leave an orphaned FunctionResultContent — an OpenAI/Azure 400.
        // The pair must be treated as one unit: because the result is pinned, the whole unit is kept and the plain filler
        // message is dropped instead.
        var messages = new List<ChatMessage>
        {
            System("sys"), // 0: protected (system)
            User(new string('x', 400)), // 1: droppable filler (the trim target)
            AssistantToolCall("c1", "search"), // 2: droppable — the tool call
            ToolResult("c1", "r1"), // 3: protected (recent-keep) — the matching result
            User("final") // 4: protected (last)
        };

        var result = ProviderCallBudgeter.Budget(messages, instructionsTokens: 0, effectiveWindowTokens: 30, Options(recentKeep: 2));

        AssertEx.True(result.Trimmed);
        AssertEx.False(ContainsText(result.Messages, new string('x', 400)), "the plain filler is dropped to make room");
        AssertEx.True(ContainsCall(result.Messages, "c1"), "the tool call must be kept because its result is protected");
        AssertEx.True(ContainsResult(result.Messages, "c1"), "the protected tool result is never dropped");
    }

    [Test]
    public void Budget_DropsToolCallUnitWhole_WhenAllMembersDroppable_NeverOrphans()
    {
        // The whole call/result pair is old and droppable. It must be dropped as one unit — never the result kept without
        // its call, nor the call kept without its result.
        var messages = new List<ChatMessage>
        {
            System("sys"), // 0: protected
            AssistantToolCall("c1", "search"), // 1: droppable — the tool call
            ToolResult("c1", new string('y', 400)), // 2: droppable — the matching result (oversized-but-under-excerpt)
            User("u1"), // 3: droppable filler
            Assistant("a1"), // 4: protected (recent-keep)
            User("final") // 5: protected (last)
        };

        var result = ProviderCallBudgeter.Budget(messages, instructionsTokens: 0, effectiveWindowTokens: 40, Options(recentKeep: 2));

        AssertEx.True(result.Trimmed);
        AssertEx.False(ContainsCall(result.Messages, "c1"), "the tool call is dropped with its unit");
        AssertEx.False(ContainsResult(result.Messages, "c1"), "the tool result is dropped with its call — never orphaned");
        AssertEx.True(ContainsText(result.Messages, "final"), "the last message is always kept");
    }

    [Test]
    public void Budget_MultiCallMessage_DropsMessageAndAllResultsTogether()
    {
        // One assistant turn issues TWO calls whose results land in SEPARATE tool messages. All three messages form one
        // unit (chained through the shared assistant message) and must drop together — leaving either result without its
        // call, or the call message without a result, is a 400.
        var messages = new List<ChatMessage>
        {
            System("sys"), // 0: protected
            AssistantMultiCall(("c1", "s1"), ("c2", "s2")), // 1: droppable — two calls
            ToolResult("c1", new string('y', 400)), // 2: droppable — result for c1
            ToolResult("c2", new string('z', 400)), // 3: droppable — result for c2
            User("u1"), // 4: droppable filler
            Assistant("a1"), // 5: protected (recent-keep)
            User("final") // 6: protected (last)
        };

        var result = ProviderCallBudgeter.Budget(messages, instructionsTokens: 0, effectiveWindowTokens: 60, Options(recentKeep: 2));

        AssertEx.True(result.Trimmed);
        AssertEx.False(ContainsCall(result.Messages, "c1"), "the multi-call message is dropped with its unit");
        AssertEx.False(ContainsCall(result.Messages, "c2"), "both calls in the multi-call message drop together");
        AssertEx.False(ContainsResult(result.Messages, "c1"), "the first result drops with its call");
        AssertEx.False(ContainsResult(result.Messages, "c2"), "the second result drops with its call — never orphaned");
    }

    [Test]
    public void Budget_MultiCallMessage_KeepsWholeUnit_WhenOneResultIsProtected()
    {
        // A multi-call assistant turn where only the SECOND result is in the protected window. The unit is pinned by that
        // one protected member, so the whole thing — both calls and both results, including the droppable-range first
        // result — is kept, and a plain filler message is dropped instead.
        var messages = new List<ChatMessage>
        {
            System("sys"), // 0: protected
            User(new string('x', 400)), // 1: droppable filler (the trim target)
            AssistantMultiCall(("c1", "s1"), ("c2", "s2")), // 2: droppable-range — two calls
            ToolResult("c1", "r1"), // 3: droppable-range — result for c1
            ToolResult("c2", "r2"), // 4: protected (recent-keep) — result for c2
            User("final") // 5: protected (last)
        };

        var result = ProviderCallBudgeter.Budget(messages, instructionsTokens: 0, effectiveWindowTokens: 40, Options(recentKeep: 2));

        AssertEx.True(result.Trimmed);
        AssertEx.False(ContainsText(result.Messages, new string('x', 400)), "the plain filler is dropped to make room");
        AssertEx.True(ContainsCall(result.Messages, "c1"), "both calls are kept because a sibling result is protected");
        AssertEx.True(ContainsCall(result.Messages, "c2"), "both calls are kept because a sibling result is protected");
        AssertEx.True(ContainsResult(result.Messages, "c1"), "the droppable-range result is kept with its pinned unit");
        AssertEx.True(ContainsResult(result.Messages, "c2"), "the protected result is kept");
    }

    /// <summary>
    ///     Model-matrix 4,096 window: round 2 overshot only because round 1's reasoning was still counted. Everything here sits in the
    ///     recent window, so no drop can help; stripping the superseded reasoning does.
    /// </summary>
    [Test]
    public void Budget_StripsSupersededReasoning_KeepsTheInFlightRoundsReasoningAndTheUserMessage()
    {
        var question = $"Calculate it. {new string('q', 200)}";
        var oldReasoning = new string('r', 400);
        var currentReasoning = new string('s', 400);
        var messages = new List<ChatMessage>
        {
            System("sys"),
            User(question),
            AssistantReasonedCall(oldReasoning, "c1", "calculate"),
            ToolResult("c1", "42"),
            AssistantReasonedCall(currentReasoning, "c2", "calculate"),
            ToolResult("c2", "84")
        };
        var total = ProviderMessageTokenEstimator.EstimateTokens(messages, 4);

        var result = ProviderCallBudgeter.Budget(messages, instructionsTokens: 0, effectiveWindowTokens: total - 50, Options());

        AssertEx.False(result.ExceedsWindow, "stripping round 1's reasoning frees ~100 tokens, enough for a 50-token overshoot");
        AssertEx.True(result.Trimmed);
        AssertEx.Equal(expected: 1, result.ReasoningStripped);
        AssertEx.Equal(expected: 0, result.MessagesDropped);
        AssertEx.Equal(total - (400 / 4), result.EstimatedTokensAfter);
        AssertEx.False(ContainsReasoning(result.Messages, oldReasoning), "the superseded reasoning is stripped");
        AssertEx.True(ContainsReasoning(result.Messages, currentReasoning), "the in-flight round keeps its reasoning");
        AssertEx.True(ContainsText(result.Messages, question), "the user's current message is kept");
        AssertEx.True(ContainsCall(result.Messages, "c1") && ContainsResult(result.Messages, "c1"), "the stripped round keeps its call and result");
    }

    [Test]
    public void Budget_StripsAReasoningOnlyMessageWhole()
    {
        var messages = new List<ChatMessage>
        {
            User("u0"),
            new(ChatRole.Assistant, [new TextReasoningContent(new string('r', 400))]),
            AssistantToolCall("c1", "calculate"),
            ToolResult("c1", "42")
        };
        var total = ProviderMessageTokenEstimator.EstimateTokens(messages, 4);

        var result = ProviderCallBudgeter.Budget(messages, instructionsTokens: 0, effectiveWindowTokens: total - 50, Options());

        AssertEx.False(result.ExceedsWindow);
        AssertEx.Equal(expected: 1, result.ReasoningStripped);
        AssertEx.Equal(expected: 1, result.MessagesDropped, "an emptied message is dropped, never sent contentless");
        AssertEx.Equal(expected: 3, result.Messages.Count);
    }

    [Test]
    public void Budget_UnderWindow_LeavesSupersededReasoningInPlace()
    {
        var oldReasoning = new string('r', 400);
        var messages = new List<ChatMessage>
        {
            User("u0"),
            AssistantReasonedCall(oldReasoning, "c1", "calculate"),
            ToolResult("c1", "42"),
            AssistantReasonedCall("now", "c2", "calculate"),
            ToolResult("c2", "84")
        };
        var total = ProviderMessageTokenEstimator.EstimateTokens(messages, 4);

        var result = ProviderCallBudgeter.Budget(messages, instructionsTokens: 0, effectiveWindowTokens: total, Options());

        AssertEx.False(result.Trimmed);
        AssertEx.Equal(expected: 0, result.ReasoningStripped);
        AssertEx.True(ReferenceEquals(messages, result.Messages), "a round that fits is not touched");
    }

    [Test]
    public void Budget_StillOverAfterStrippingReasoning_FlagsTheRound_AndNeverStripsTheInFlightRound()
    {
        var question = $"Calculate it. {new string('q', 200)}";
        var currentReasoning = new string('s', 400);
        var messages = new List<ChatMessage>
        {
            System("sys"),
            User(question),
            AssistantReasonedCall(new string('r', 40), "c1", "calculate"),
            ToolResult("c1", "42"),
            AssistantReasonedCall(currentReasoning, "c2", "calculate"),
            ToolResult("c2", "84")
        };
        var total = ProviderMessageTokenEstimator.EstimateTokens(messages, 4);

        // Round 1's reasoning frees 10 tokens; only stripping the in-flight round's 100 would fit.
        var result = ProviderCallBudgeter.Budget(messages, instructionsTokens: 0, effectiveWindowTokens: total - 50, Options());

        AssertEx.True(result.ExceedsWindow, "an irreducible round still fails as a context-window error");
        AssertEx.Equal(expected: 1, result.ReasoningStripped);
        AssertEx.True(ContainsReasoning(result.Messages, currentReasoning), "the in-flight round's reasoning is never stripped");
        AssertEx.True(ContainsText(result.Messages, question), "the user's current message is kept");
    }

    /// <summary>
    ///     Codex's stateless tool loop replays each retained call's reasoning item before it: raw item (non-streaming) or
    ///     protected data (streaming). Neither may be stripped, so the round is flagged exactly as without Pass 3.
    /// </summary>
    [Test]
    public void Budget_NeverStripsProviderBackedReasoning_FromAnyRetainedRound()
    {
        var rawReasoning = new string('r', 400);
        var protectedReasoning = new string('p', 400);
        var messages = new List<ChatMessage>
        {
            System("sys"),
            User("Calculate it."),
            ProviderReasonedCall(new TextReasoningContent(rawReasoning)
            {
                RawRepresentation = new object()
            }, "c1"),
            ToolResult("c1", "42"),
            ProviderReasonedCall(new TextReasoningContent(protectedReasoning)
            {
                ProtectedData = "encrypted"
            }, "c2"),
            ToolResult("c2", "84"),
            ProviderReasonedCall(new TextReasoningContent("now")
            {
                ProtectedData = "encrypted"
            }, "c3"),
            ToolResult("c3", "126")
        };
        var total = ProviderMessageTokenEstimator.EstimateTokens(messages, 4);

        var result = ProviderCallBudgeter.Budget(messages, instructionsTokens: 0, effectiveWindowTokens: total - 50, Options(recentKeep: 8));

        AssertEx.True(result.ExceedsWindow, "nothing here is reducible, so the round fails as a context-window error");
        AssertEx.False(result.Trimmed);
        AssertEx.Equal(expected: 0, result.ReasoningStripped);
        AssertEx.True(ReferenceEquals(messages, result.Messages), "the provider-backed history is passed through untouched");
    }

    [Test]
    public void Budget_MixedReasoningMessage_LosesOnlyThePlainReasoning()
    {
        var plainReasoning = new string('r', 400);
        var protectedPart = new TextReasoningContent("summary")
        {
            ProtectedData = "encrypted"
        };
        var messages = new List<ChatMessage>
        {
            User("Calculate it."),
            new(ChatRole.Assistant, [new TextReasoningContent(plainReasoning), protectedPart, new FunctionCallContent("c1", "calculate", new Dictionary<string, object?>())]),
            ToolResult("c1", "42"),
            new(ChatRole.Assistant, [new TextReasoningContent(plainReasoning), protectedPart]),
            AssistantToolCall("c2", "calculate"),
            ToolResult("c2", "84")
        };
        var total = ProviderMessageTokenEstimator.EstimateTokens(messages, 4);

        var result = ProviderCallBudgeter.Budget(messages, instructionsTokens: 0, effectiveWindowTokens: total - 150, Options());

        AssertEx.False(result.ExceedsWindow);
        AssertEx.Equal(expected: 2, result.ReasoningStripped);
        AssertEx.Equal(expected: 0, result.MessagesDropped, "a message keeping provider-backed reasoning is not emptied, so never dropped");
        AssertEx.Equal(expected: 6, result.Messages.Count);
        AssertEx.False(ContainsReasoning(result.Messages, plainReasoning), "the plain reasoning is stripped from both messages");
        AssertEx.Equal(expected: 2, result.Messages.SelectMany(static message => message.Contents).Count(content => ReferenceEquals(content, protectedPart)),
            "the provider-backed part survives in both messages");
    }

    private static ChatMessage ProviderReasonedCall(TextReasoningContent reasoning, string callId)
    {
        return new ChatMessage(ChatRole.Assistant, [reasoning, new FunctionCallContent(callId, "calculate", new Dictionary<string, object?>())]);
    }

    private static ProviderCallBudgetOptions Options(int recentKeep = 6, int excerptChars = 2000)
    {
        return new ProviderCallBudgetOptions
        {
            RecentMessagesToKeep = recentKeep,
            OversizedToolResultExcerptChars = excerptChars
        };
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

    private static ChatMessage AssistantReasonedCall(string reasoning, string callId, string name)
    {
        return new ChatMessage(ChatRole.Assistant,
            [new TextReasoningContent(reasoning), new FunctionCallContent(callId, name, new Dictionary<string, object?>())]);
    }

    private static bool ContainsReasoning(IReadOnlyList<ChatMessage> messages, string reasoning)
    {
        return messages.Any(message => message.Contents.OfType<TextReasoningContent>().Any(part => string.Equals(part.Text, reasoning, StringComparison.Ordinal)));
    }

    private static ChatMessage AssistantMultiCall(params (string CallId, string Name)[] calls)
    {
        return new ChatMessage(ChatRole.Assistant,
            [.. calls.Select(call => new FunctionCallContent(call.CallId, call.Name, new Dictionary<string, object?>()))]);
    }

    private static ChatMessage ToolResult(string callId, string result)
    {
        return new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, result)]);
    }

    private static bool ContainsCall(IReadOnlyList<ChatMessage> messages, string callId)
    {
        return messages.SelectMany(message => message.Contents.OfType<FunctionCallContent>())
                       .Any(call => string.Equals(call.CallId, callId, StringComparison.Ordinal));
    }

    private static bool ContainsResult(IReadOnlyList<ChatMessage> messages, string callId)
    {
        return messages.SelectMany(message => message.Contents.OfType<FunctionResultContent>())
                       .Any(result => string.Equals(result.CallId, callId, StringComparison.Ordinal));
    }

    private static bool ContainsText(IReadOnlyList<ChatMessage> messages, string needle)
    {
        return messages.Any(message => message.Contents.OfType<TextContent>().Any(text => (text.Text ?? string.Empty).Contains(needle, StringComparison.Ordinal)));
    }

    private static string FindToolResultText(IReadOnlyList<ChatMessage> messages, string callId)
    {
        var result = messages.SelectMany(message => message.Contents.OfType<FunctionResultContent>())
                             .First(content => string.Equals(content.CallId, callId, StringComparison.Ordinal));
        return result.Result?.ToString() ?? string.Empty;
    }
}
