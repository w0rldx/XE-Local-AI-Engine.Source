namespace XE_Local_AI_Engine.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The live capture appends one part per stream delta; storage and the judge both want the coalesced form. A
///     regression here is not cosmetic: the per-delta transcript is what blew the judge's context window.
/// </summary>
public sealed class BenchmarkOutputPartsTests
{
    [Test]
    public void Coalesce_MergesAdjacentSameKindTextAndKeepsOrderAcrossAToolBoundary()
    {
        BenchmarkOutputPart[] streamed =
        [
            new("reasoning", Content: "the"),
            new("reasoning", Content: " 5"),
            new("output", Content: "par"),
            new("output", Content: "tial"),
            new("tool_call", ToolCallId: "call-1", ToolName: "search", Arguments: "{}"),
            new("tool_result", ToolCallId: "call-1", ToolName: "search", Result: "ok", IsError: false),
            new("output", Content: "fin"),
            new("output", Content: "al")
        ];

        var coalesced = BenchmarkOutputParts.Coalesce(streamed);

        AssertEx.Equal(expected: 5, coalesced.Count);
        AssertEx.Equal("reasoning", coalesced[0].Kind);
        AssertEx.Equal("the 5", coalesced[0].Content);
        AssertEx.Equal("output", coalesced[1].Kind);
        AssertEx.Equal("partial", coalesced[1].Content);
        AssertEx.Equal("tool_call", coalesced[2].Kind);
        AssertEx.Equal("call-1", coalesced[2].ToolCallId);
        AssertEx.Equal("{}", coalesced[2].Arguments);
        AssertEx.Equal("tool_result", coalesced[3].Kind);
        AssertEx.Equal("ok", coalesced[3].Result);

        // Text after a tool boundary must never merge into text before it.
        AssertEx.Equal("output", coalesced[4].Kind);
        AssertEx.Equal("final", coalesced[4].Content);
    }

    [Test]
    public void ForJudge_DropsReasoningKeepsTextAndToolParts()
    {
        BenchmarkOutputPart[] streamed =
        [
            new("reasoning", Content: "hidden"),
            new("reasoning", Content: " thoughts"),
            new("output", Content: "visible"),
            new("tool_call", ToolCallId: "call-1", ToolName: "search", Arguments: "{}"),
            new("tool_result", ToolCallId: "call-1", ToolName: "search", Result: "ok", IsError: false),
            new("reasoning", Content: "more thinking"),
            new("output", Content: " answer")
        ];

        var graded = BenchmarkOutputParts.ForJudge(streamed, judgeContextTokens: 16384);

        AssertEx.Empty(graded.Where(static part => part.Kind == "reasoning"));
        AssertEx.Equal(expected: 4, graded.Count);
        AssertEx.Equal("visible", graded[0].Content);
        AssertEx.Equal("tool_call", graded[1].Kind);
        AssertEx.Equal("tool_result", graded[2].Kind);
        AssertEx.Equal(" answer", graded[3].Content);
    }

    [Test]
    public void ForJudge_WhenTheGradedTextStillOverrunsTheWindow_CutsTheTailAndMarksIt()
    {
        var overlong = new string('x', 100_000);
        BenchmarkOutputPart[] streamed =
        [
            new("output", Content: overlong),
            new("output", Content: "tail that no longer fits")
        ];

        var graded = BenchmarkOutputParts.ForJudge(streamed, judgeContextTokens: 4096);

        // 4096 tokens / 2 * 4 chars = 8192 characters of answer, then the marker.
        var kept = AssertEx.NotNull(graded[0].Content);
        AssertEx.Equal(expected: 1, graded.Count);
        AssertEx.Equal(8192 + BenchmarkOutputParts.TruncationMarker.Length, kept.Length);
        AssertEx.True(kept.EndsWith(BenchmarkOutputParts.TruncationMarker, StringComparison.Ordinal));
    }

    [Test]
    public void ForJudge_WhenTheAnswerFits_ReturnsItWholeWithNoMarker()
    {
        BenchmarkOutputPart[] streamed = [new("output", Content: new string('x', 4096))];

        var graded = BenchmarkOutputParts.ForJudge(streamed, judgeContextTokens: 16384);

        AssertEx.Equal(expected: 4096, AssertEx.NotNull(graded[0].Content).Length);
    }
}
