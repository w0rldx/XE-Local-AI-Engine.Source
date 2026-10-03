namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class NodeChatPartAccumulatorTests
{
    [Test]
    public void AppendReasoning_ThenToolRequested_ThenAppendReasoning_ProducesThreePartsInOrder()
    {
        var acc = new NodeChatPartAccumulator();

        acc.AppendReasoning("before", sequence: 0);
        acc.AppendToolRequested("call-1", "GetCurrentTime", "{}", requiresApproval: false, sequence: 1);
        acc.AppendReasoning("after", sequence: 2);

        var parts = acc.Snapshot();

        AssertEx.Equal(expected: 3, parts.Count);
        AssertEx.Equal(NodeChatMessagePartKinds.Reasoning, parts[0].Kind);
        AssertEx.Equal("before", parts[0].Text);
        AssertEx.Equal(expected: 0, parts[0].Sequence);
        AssertEx.Equal(NodeChatMessagePartKinds.Tool, parts[1].Kind);
        AssertEx.Equal("call-1", parts[1].ToolCallId);
        AssertEx.Equal(expected: 1, parts[1].Sequence);
        AssertEx.Equal(NodeChatMessagePartKinds.Reasoning, parts[2].Kind);
        AssertEx.Equal("after", parts[2].Text);
        AssertEx.Equal(expected: 2, parts[2].Sequence);
    }

    [Test]
    public void AppendToolRequested_WithAContentOffset_ClosesTheInterimTextIntoATextPartAheadOfTheTool()
    {
        // Two model rounds with a tool call between: narration, then the call, then the answer.
        const string interim = "Let me search for that. ";
        const string content = interim + "The answer is 42.";
        var acc = new NodeChatPartAccumulator();

        acc.AppendReasoning("plan", sequence: 1);
        acc.AppendToolRequested("call-1", "web_search", "{}", requiresApproval: false, sequence: 2, contentOffset: interim.Length);
        acc.CompleteToolCall("call-1", "web_search", "results", isError: false, sequence: 3);

        var parts = acc.Snapshot(content);

        AssertEx.Equal(expected: 3, parts.Count);
        AssertEx.Equal(NodeChatMessagePartKinds.Reasoning, parts[0].Kind);
        AssertEx.Equal(NodeChatMessagePartKinds.Text, parts[1].Kind);
        AssertEx.Equal(interim, parts[1].Text);
        AssertEx.Equal(expected: 2, parts[1].Sequence);
        AssertEx.Equal(NodeChatMessagePartKinds.Tool, parts[2].Kind);
        AssertEx.Equal(expected: 2, parts[2].Sequence);
        AssertEx.Equal("The answer is 42.", acc.FinalSegment(content));
    }

    [Test]
    public void FinalSegment_WithoutAnyToolCall_IsTheWholeContentAndNoTextPartIsEmitted()
    {
        var acc = new NodeChatPartAccumulator();
        acc.AppendReasoning("thinking", sequence: 1);

        AssertEx.Equal("Plain answer.", acc.FinalSegment("Plain answer."));
        AssertEx.False(acc.Snapshot("Plain answer.").Any(static part => part.Kind == NodeChatMessagePartKinds.Text));
    }

    [Test]
    public void AppendToolRequested_SplitsOncePerNewText_SkipsBlankNarration_AndIgnoresARepeatedRequest()
    {
        // A call with nothing before it, a whitespace-only gap, a repeated requested phase with a later offset, and a
        // second call after real narration: only the narration becomes a part, and the answer starts after the last call.
        const string content = "\n\nNow the second lookup. Final.";
        var acc = new NodeChatPartAccumulator();

        acc.AppendToolRequested("call-0", "web_search", "{}", requiresApproval: false, sequence: 1, contentOffset: 0);
        acc.AppendToolRequested("call-1", "web_search", "{}", requiresApproval: false, sequence: 2, contentOffset: 2);
        acc.AppendToolRequested("call-1", "web_search", "{}", requiresApproval: false, sequence: 3, contentOffset: 10);
        acc.AppendToolRequested("call-2", "web_fetch", "{}", requiresApproval: false, sequence: 4, contentOffset: "\n\nNow the second lookup. ".Length);

        var parts = acc.Snapshot(content);

        AssertEx.Equal("tool,tool,text,tool", string.Join(',', parts.Select(static part => part.Kind)));
        AssertEx.Equal("Now the second lookup. ", parts[2].Text);
        AssertEx.Equal(expected: 4, parts[2].Sequence);
        AssertEx.Equal("Final.", acc.FinalSegment(content));
    }

    [Test]
    public void Snapshot_WithoutContent_EmitsNoTextPart_ForCallersThatKeepTheWholeText()
    {
        var acc = new NodeChatPartAccumulator();
        acc.AppendToolRequested("call-1", "web_search", "{}", requiresApproval: false, sequence: 1, contentOffset: 12);

        var parts = acc.Snapshot();

        AssertEx.Equal(expected: 1, parts.Count);
        AssertEx.Equal(NodeChatMessagePartKinds.Tool, parts[0].Kind);
    }

    [Test]
    public void FinalSegment_WhenNoAnswerFollowsTheLastCall_KeepsTheWholeTextAndSnapshotEmitsNoTextPart()
    {
        // The turn ended right after a tool call (or a faulted terminal's persisted text ends before the offset): the
        // pre-split behaviour holds, so history, previews and memory see the whole text and no narration part exists.
        const string interim = "Let me search for that. ";
        var acc = new NodeChatPartAccumulator();
        acc.AppendToolRequested("call-1", "web_search", "{}", requiresApproval: false, sequence: 1, contentOffset: interim.Length);

        AssertEx.Equal(interim + "  \n", acc.FinalSegment(interim + "  \n"));
        AssertEx.Equal("tool", string.Join(',', acc.Snapshot(interim + "  \n").Select(static part => part.Kind)));
        AssertEx.Equal("Let me", acc.FinalSegment("Let me"));
        AssertEx.Equal("tool", string.Join(',', acc.Snapshot("Let me").Select(static part => part.Kind)));
    }

    [Test]
    public void AccumulateToolPart_WithAnImageResult_PersistsTheImageAsItsOwnPartAfterTheCard()
    {
        var acc = new NodeChatPartAccumulator();
        var image = new DataContent(new byte[]
        {
            1,
            2,
            3
        }, "image/png");
        var oversized = new DataContent(new byte[(1024 * 1024) + 1], "image/png");
        var audio = new DataContent(new byte[]
        {
            4
        }, "audio/wav");
        ChatStreamEventMapper.AccumulateToolPart(acc, Lifecycle(ToolCallLifecyclePhase.Requested, []), sequence: 1);

        ChatStreamEventMapper.AccumulateToolPart(acc, Lifecycle(ToolCallLifecyclePhase.Completed, [image, oversized, audio]), sequence: 2);

        var parts = acc.Snapshot();
        AssertEx.Equal(expected: 2, parts.Count, "Only the image within the size cap is persisted; audio keeps its placeholder text only.");
        AssertEx.Equal("[image image/png, 1 KB]", parts[0].Result, "The card keeps the model's text, never a type name.");
        AssertEx.Equal(NodeChatMessagePartKinds.Image, parts[1].Kind);
        AssertEx.Equal("call-1", parts[1].ToolCallId);
        AssertEx.Equal("image/png", parts[1].Name);
        AssertEx.Equal("data:image/png;base64,AQID", parts[1].Text);
    }

    [Test]
    public void AccumulateToolPart_ManySmallImages_StopAtThePerMessageCeiling()
    {
        // Codex review 2026-09-30: the 1 MiB per-image cap alone let 100 images of 900 KiB grow one metadata row past 100 MiB.
        var acc = new NodeChatPartAccumulator();
        var images = Enumerable.Range(0, NodeChatPartAccumulator.MaxToolImagesPerMessage + 3)
                               .Select(static _ => new DataContent(new byte[16], "image/png"))
                               .ToList();
        ChatStreamEventMapper.AccumulateToolPart(acc, Lifecycle(ToolCallLifecyclePhase.Requested, []), sequence: 1);

        ChatStreamEventMapper.AccumulateToolPart(acc, Lifecycle(ToolCallLifecyclePhase.Completed, images), sequence: 2);

        var imageParts = acc.Snapshot().Count(static part => part.Kind == NodeChatMessagePartKinds.Image);
        AssertEx.Equal(NodeChatPartAccumulator.MaxToolImagesPerMessage, imageParts, "the count ceiling holds");

        var bytesAcc = new NodeChatPartAccumulator();
        var big = Enumerable.Range(0, 6).Select(static _ => new DataContent(new byte[900 * 1024], "image/png")).ToList();
        ChatStreamEventMapper.AccumulateToolPart(bytesAcc, Lifecycle(ToolCallLifecyclePhase.Requested, []), sequence: 1);

        ChatStreamEventMapper.AccumulateToolPart(bytesAcc, Lifecycle(ToolCallLifecyclePhase.Completed, big), sequence: 2);

        var persistedBytes = bytesAcc.Snapshot().Where(static part => part.Kind == NodeChatMessagePartKinds.Image).Sum(static part => part.Text!.Length);
        AssertEx.True(persistedBytes <= NodeChatPartAccumulator.MaxToolImageBytesPerMessage, $"the byte ceiling holds ({persistedBytes})");
        AssertEx.True(persistedBytes > 0, "the first images still persist");
    }

    private static ToolCallLifecyclePayload Lifecycle(ToolCallLifecyclePhase phase, IReadOnlyList<DataContent> media) =>
        new()
        {
            InvocationId = Guid.NewGuid(),
            ToolCallId = "call-1",
            ToolName = "mcp__srv__shot",
            Phase = phase,
            Result = phase == ToolCallLifecyclePhase.Completed ? "[image image/png, 1 KB]" : null,
            Media = media
        };

    [Test]
    public void AppendReasoning_MultipleDeltas_BeforeAnyTool_ExtendsTheSameSegment()
    {
        var acc = new NodeChatPartAccumulator();

        acc.AppendReasoning("chunk1", sequence: 0);
        acc.AppendReasoning("chunk2", sequence: 3); // higher sequence — same segment extended
        acc.AppendReasoning("chunk3", sequence: 5);

        var parts = acc.Snapshot();

        AssertEx.Equal(expected: 1, parts.Count);
        AssertEx.Equal(NodeChatMessagePartKinds.Reasoning, parts[0].Kind);
        AssertEx.Equal("chunk1chunk2chunk3", parts[0].Text);
        AssertEx.Equal(expected: 0, parts[0].Sequence); // stamped at open
    }

    [Test]
    public void AppendReasoning_AfterTool_OpensNewSegmentNotExtendingPriorOne()
    {
        var acc = new NodeChatPartAccumulator();

        acc.AppendReasoning("first segment", sequence: 0);
        acc.AppendToolRequested("call-1", "DoThing", args: null, requiresApproval: false, sequence: 1);
        acc.AppendReasoning("second ", sequence: 2);
        acc.AppendReasoning("segment", sequence: 4); // extends the second segment

        var parts = acc.Snapshot();

        AssertEx.Equal(expected: 3, parts.Count);
        AssertEx.Equal("first segment", parts[0].Text);
        AssertEx.Equal("second segment", parts[2].Text);
        AssertEx.Equal(expected: 2, parts[2].Sequence); // stamped when 2nd segment opened
    }

    [Test]
    public void AppendToolRequested_DuplicateCallId_DoesNotAddSecondToolPart()
    {
        var acc = new NodeChatPartAccumulator();

        acc.AppendToolRequested("call-1", "GetCurrentTime", "{}", requiresApproval: false, sequence: 0);
        acc.AppendToolRequested("call-1", "GetCurrentTime", "{}", requiresApproval: false, sequence: 1); // duplicate

        var parts = acc.Snapshot();

        AssertEx.Equal(expected: 1, parts.Count);
        AssertEx.Equal("call-1", parts[0].ToolCallId);
        AssertEx.Equal(expected: 0, parts[0].Sequence); // first open wins
    }

    [Test]
    public void CompleteToolCall_WhenPriorRequested_CollapsesSetsResultAndReceivedState()
    {
        var acc = new NodeChatPartAccumulator();

        acc.AppendToolRequested("call-1", "GetCurrentTime", "{\"tz\":\"UTC\"}", requiresApproval: false, sequence: 0);
        acc.CompleteToolCall("call-1", "GetCurrentTime", "2026-06-01T00:00:00Z", isError: false, sequence: 1);

        var parts = acc.Snapshot();

        AssertEx.Equal(expected: 1, parts.Count);
        var tool = parts[0];
        AssertEx.Equal(NodeChatMessagePartKinds.Tool, tool.Kind);
        AssertEx.Equal("call-1", tool.ToolCallId);
        AssertEx.Equal(NodeChatToolPartStates.Received, tool.State);
        AssertEx.Equal("2026-06-01T00:00:00Z", tool.Result);
        AssertEx.Equal("{\"tz\":\"UTC\"}", tool.Args); // args preserved from requested phase
        AssertEx.Equal(expected: 0, tool.Sequence); // stamped at requested open, not completed
    }

    [Test]
    public void CompleteToolCall_WhenIsError_SetsFailedState()
    {
        var acc = new NodeChatPartAccumulator();

        acc.AppendToolRequested("call-err", "RunScript", "{}", requiresApproval: true, sequence: 0);
        acc.CompleteToolCall("call-err", "RunScript", "permission denied", isError: true, sequence: 1);

        var parts = acc.Snapshot();

        AssertEx.Equal(expected: 1, parts.Count);
        AssertEx.Equal(NodeChatToolPartStates.Failed, parts[0].State);
        AssertEx.Equal("permission denied", parts[0].Result);
    }

    [Test]
    public void CompleteToolCall_WithoutPriorRequested_AddsDefensiveToolPart()
    {
        var acc = new NodeChatPartAccumulator();

        // Completed arrives with no prior Requested (defensive path at ~:92 in the accumulator)
        acc.CompleteToolCall("call-orphan", "SomeTool", "result text", isError: false, sequence: 5);

        var parts = acc.Snapshot();

        AssertEx.Equal(expected: 1, parts.Count);
        var tool = parts[0];
        AssertEx.Equal(NodeChatMessagePartKinds.Tool, tool.Kind);
        AssertEx.Equal("call-orphan", tool.ToolCallId);
        AssertEx.Equal("SomeTool", tool.Name);
        AssertEx.Equal(NodeChatToolPartStates.Received, tool.State);
        AssertEx.Equal("result text", tool.Result);
        AssertEx.Equal(expected: 5, tool.Sequence);
    }

    [Test]
    public void CompleteToolCall_WithoutPriorRequested_SubsequentDuplicateCompletedIsIgnored()
    {
        var acc = new NodeChatPartAccumulator();

        acc.CompleteToolCall("call-orphan", "SomeTool", "first result", isError: false, sequence: 5);
        // A second Completed for the same id (e.g. idempotent re-delivery) collapses into the existing entry.
        acc.CompleteToolCall("call-orphan", "SomeTool", "second result", isError: false, sequence: 6);

        var parts = acc.Snapshot();

        // Still only one part; the second complete updates state/result on the existing entry.
        AssertEx.Equal(expected: 1, parts.Count);
        AssertEx.Equal("second result", parts[0].Result);
    }

    [Test]
    public void Snapshot_SortsBySequence_EvenWhenHigherSequenceToolAddedBeforeLowerSequenceReasoning()
    {
        var acc = new NodeChatPartAccumulator();

        // Simulate concurrent feed: tool part stamped at seq=1, then reasoning delta at seq=0
        // (lower sequence added after — positional insertion is out of order).
        acc.AppendToolRequested("call-1", "GetCurrentTime", args: null, requiresApproval: false, sequence: 1);
        acc.AppendReasoning("pre-tool thinking", sequence: 0);

        var parts = acc.Snapshot();

        AssertEx.Equal(expected: 2, parts.Count);
        // Snapshot must reorder by sequence: seq=0 reasoning first, seq=1 tool second.
        AssertEx.Equal(NodeChatMessagePartKinds.Reasoning, parts[0].Kind);
        AssertEx.Equal(expected: 0, parts[0].Sequence);
        AssertEx.Equal(NodeChatMessagePartKinds.Tool, parts[1].Kind);
        AssertEx.Equal(expected: 1, parts[1].Sequence);
    }

    [Test]
    public void Snapshot_MultipleCallsReturnConsistentView()
    {
        var acc = new NodeChatPartAccumulator();
        acc.AppendReasoning("thinking", sequence: 0);
        acc.AppendToolRequested("call-1", "GetCurrentTime", args: null, requiresApproval: false, sequence: 1);
        acc.CompleteToolCall("call-1", "GetCurrentTime", "now", isError: false, sequence: 2);

        var first = acc.Snapshot();
        var second = acc.Snapshot();

        // Both snapshots reflect the same state; they are independent immutable copies.
        AssertEx.Equal(first.Count, second.Count);
        AssertEx.Equal(first[0].Kind, second[0].Kind);
        AssertEx.Equal<string?>(first[1].State, second[1].State);
    }

    [Test]
    public void HasParts_WhenEmpty_ReturnsFalse()
    {
        var acc = new NodeChatPartAccumulator();

        AssertEx.False(acc.HasParts);
    }

    [Test]
    public void HasParts_AfterAnyAppend_ReturnsTrue()
    {
        var acc = new NodeChatPartAccumulator();
        acc.AppendReasoning("thinking", sequence: 0);

        AssertEx.True(acc.HasParts);
    }

    [Test]
    public void AppendReasoning_NullOrEmptyDelta_IsIgnoredAndDoesNotCreatePart()
    {
        var acc = new NodeChatPartAccumulator();

        acc.AppendReasoning(delta: null, sequence: 0);
        acc.AppendReasoning(string.Empty, sequence: 1);

        AssertEx.False(acc.HasParts);
        AssertEx.Equal(expected: 0, acc.Snapshot().Count);
    }
}
