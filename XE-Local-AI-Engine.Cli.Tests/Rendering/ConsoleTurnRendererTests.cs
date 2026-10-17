namespace XE_Local_AI_Engine.Cli.Tests.Rendering;

using System.Text.Json;
using XE_Local_AI_Engine.Cli.Rendering;
using XE_Local_AI_Engine.Cli.Streaming;
using XE_Local_AI_Engine.Cli.Tests.Commands;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     What <see cref="ConsoleTurnRenderer" /> prints when the node restates text it already streamed (a snapshot, or a
///     terminal carrying only the answer after the last tool call), and how it shows tool arguments.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ConsoleTurnRendererTests
{
    private const string Replaced = "the node replaced";

    [Test]
    public async Task Snapshot_ThatExtendsWhatWasShown_PrintsOnlyTheUnseenSuffixAndNoNotice()
    {
        await using var content = new StringWriter();
        await using var side = new StringWriter();
        using var renderer = Renderer(content, side, sameScreen: false);

        await renderer.RenderAsync(Event(CliStreamEventKind.Delta, ChatStreamEventTypes.AssistantDelta, text: "Hel", reasoning: "plan"), CancellationToken.None);
        await renderer.RenderAsync(Event(CliStreamEventKind.Snapshot, ChatStreamEventTypes.AssistantSnapshot, text: "Hello", reasoning: "plan more"), CancellationToken.None);
        await renderer.FinishAsync(new TurnOutcome(), CancellationToken.None);

        AssertEx.Equal("Hello" + Environment.NewLine, content.ToString());
        AssertEx.Contains(side.ToString().Replace(Environment.NewLine, string.Empty, StringComparison.Ordinal), "plan more");
        AssertEx.Equal(1, side.ToString().Split("plan", StringSplitOptions.None).Length - 1, "the shown reasoning is not printed twice");
        AssertEx.False(side.ToString().Contains(Replaced, StringComparison.Ordinal), "an extending snapshot needs no notice");
    }

    [Test]
    public async Task Snapshot_ThatDiffersFromWhatWasShown_PrintsTheNoticeAndTheFullText()
    {
        await using var content = new StringWriter();
        await using var side = new StringWriter();
        using var renderer = Renderer(content, side);

        await renderer.RenderAsync(Event(CliStreamEventKind.Delta, ChatStreamEventTypes.AssistantDelta, text: "Hello", reasoning: "first idea"), CancellationToken.None);
        await renderer.RenderAsync(Event(CliStreamEventKind.Snapshot, ChatStreamEventTypes.AssistantSnapshot, text: "Goodbye", reasoning: "second idea"), CancellationToken.None);
        await renderer.FinishAsync(new TurnOutcome(), CancellationToken.None);

        AssertEx.Contains(content.ToString(), "Goodbye");
        AssertEx.Contains(side.ToString(), "(the node replaced the answer so far)");
        AssertEx.Contains(side.ToString(), "(the node replaced the reasoning so far)");
        AssertEx.Contains(side.ToString(), "second idea");
    }

    [Test]
    public async Task Terminal_CarryingOnlyTheAnswerAfterATool_PrintsOnlyItsUnseenRest()
    {
        await using var content = new StringWriter();
        await using var side = new StringWriter();
        using var renderer = Renderer(content, side);

        await renderer.RenderAsync(Event(CliStreamEventKind.Delta, ChatStreamEventTypes.AssistantDelta, text: "Let me check. "), CancellationToken.None);
        await renderer.RenderAsync(Event(CliStreamEventKind.ToolActivity, ChatStreamEventTypes.ToolCallRequested, toolName: "run_python", contentOffset: 14), CancellationToken.None);
        await renderer.RenderAsync(Event(CliStreamEventKind.ToolActivity, ChatStreamEventTypes.ToolCallCompleted, toolName: "run_python"), CancellationToken.None);
        await renderer.RenderAsync(Event(CliStreamEventKind.Delta, ChatStreamEventTypes.AssistantDelta, text: "It is"), CancellationToken.None);
        await renderer.RenderAsync(Event(CliStreamEventKind.Completed, ChatStreamEventTypes.AssistantCompleted, text: "It is 391."), CancellationToken.None);
        await renderer.FinishAsync(new TurnOutcome(), CancellationToken.None);

        AssertEx.Equal("Let me check. " + Environment.NewLine + "It is 391." + Environment.NewLine, content.ToString());
        AssertEx.False(side.ToString().Contains(Replaced, StringComparison.Ordinal), "the answer after a tool call is not a replacement");
    }

    [Test]
    public async Task ApprovalArguments_ShowQuotesAndNonAsciiLiterally()
    {
        await using var content = new StringWriter();
        await using var side = new StringWriter();
        using var renderer = Renderer(content, side);

        // Serialized the way the node does it, with the default encoder.
        var arguments = JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal) { ["code"] = "print(\"17 × 23\")" });
        AssertEx.Contains(arguments, "\\u0022", message: "the scenario needs the node's escaped form");
        await renderer.RenderAsync(Event(CliStreamEventKind.ApprovalRequested, ChatStreamEventTypes.ApprovalRequested, toolName: "run_python", arguments: arguments), CancellationToken.None);

        AssertEx.Contains(side.ToString(), """{"code":"print(\"17 × 23\")"}""");
    }

    [Test]
    public async Task JsonLines_KeepQuotesAndNonAsciiUnescaped()
    {
        await using var output = new StringWriter();

        await JsonTurnRenderer.WriteLineAsync(output, new AskJsonLine { Type = "event", Kind = "delta", Text = "It's \"17 × 23\"" }, CancellationToken.None);

        AssertEx.Contains(output.ToString(), "\"text\":\"It's \\\"17 × 23\\\"\"");
        AssertEx.False(output.ToString().Contains("\\u", StringComparison.Ordinal), "nothing is \\u-escaped");
    }

    private static ConsoleTurnRenderer Renderer(StringWriter content, StringWriter side, bool sameScreen = true) =>
        new(content, TestCli.Capture(side), sameScreen, showReasoning: true, TimeProvider.System);

    private static CliStreamEvent Event(CliStreamEventKind kind,
        string frameType,
        string text = "",
        string reasoning = "",
        string? toolName = null,
        string? arguments = null,
        long? contentOffset = null) =>
        new()
        {
            Kind = kind,
            ConversationId = Guid.Empty,
            MessageId = Guid.Empty,
            RequestId = Guid.Empty,
            Text = text,
            Reasoning = reasoning,
            Source = new ChatStreamEvent
            {
                Type = frameType,
                ToolName = toolName,
                ToolCallId = toolName is null ? null : "call-1",
                Arguments = arguments,
                ContentOffset = contentOffset
            }
        };
}
