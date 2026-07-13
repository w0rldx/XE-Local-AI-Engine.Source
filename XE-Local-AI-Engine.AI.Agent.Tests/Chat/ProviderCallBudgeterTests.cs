namespace XE_Local_AI_Engine.AI.Agent.Tests.Chat;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ProviderCallBudgeterTests
{
    [Test]
    public void Budget_WhenUnderWindow_ReturnsInputUnchangedByReference()
    {
        var messages = new List<ChatMessage> { User("hello"), Assistant("hi there") };

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

    [Test]
    public void Budget_InstructionsCountTowardTheWindow()
    {
        var messages = new List<ChatMessage> { User("u0"), Assistant("a0"), User("u1") };

        // The messages alone fit a window of 40; a large instructions estimate pushes the round over, forcing a trim.
        var underResult = ProviderCallBudgeter.Budget(messages, instructionsTokens: 0, effectiveWindowTokens: 40, Options(recentKeep: 2));
        var overResult = ProviderCallBudgeter.Budget(messages, instructionsTokens: 100, effectiveWindowTokens: 40, Options(recentKeep: 2));

        AssertEx.False(underResult.Trimmed);
        AssertEx.True(overResult.Trimmed, "the system-prompt token cost must count against the round window");
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

    private static ChatMessage ToolResult(string callId, string result)
    {
        return new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, result)]);
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
