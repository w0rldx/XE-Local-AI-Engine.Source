namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeChatPartAccumulatorTests
{
    // ── (a) reasoning → tool → reasoning ────────────────────────────────────

    [Test]
    public void AppendReasoning_ThenToolRequested_ThenAppendReasoning_ProducesThreePartsInOrder()
    {
        var acc = new NodeChatPartAccumulator();

        acc.AppendReasoning("before", sequence: 0);
        acc.AppendToolRequested("call-1", "GetCurrentTime", "{}", false, sequence: 1);
        acc.AppendReasoning("after", sequence: 2);

        var parts = acc.Snapshot();

        AssertEx.Equal(3, parts.Count);
        AssertEx.Equal(NodeChatMessagePartKinds.Reasoning, parts[0].Kind);
        AssertEx.Equal("before", parts[0].Text);
        AssertEx.Equal(0, parts[0].Sequence);
        AssertEx.Equal(NodeChatMessagePartKinds.Tool, parts[1].Kind);
        AssertEx.Equal("call-1", parts[1].ToolCallId);
        AssertEx.Equal(1, parts[1].Sequence);
        AssertEx.Equal(NodeChatMessagePartKinds.Reasoning, parts[2].Kind);
        AssertEx.Equal("after", parts[2].Text);
        AssertEx.Equal(2, parts[2].Sequence);
    }

    [Test]
    public void AppendReasoning_MultipleDeltas_BeforeAnyTool_ExtendsTheSameSegment()
    {
        var acc = new NodeChatPartAccumulator();

        acc.AppendReasoning("chunk1", sequence: 0);
        acc.AppendReasoning("chunk2", sequence: 3); // higher sequence — same segment extended
        acc.AppendReasoning("chunk3", sequence: 5);

        var parts = acc.Snapshot();

        AssertEx.Equal(1, parts.Count);
        AssertEx.Equal(NodeChatMessagePartKinds.Reasoning, parts[0].Kind);
        AssertEx.Equal("chunk1chunk2chunk3", parts[0].Text);
        AssertEx.Equal(0, parts[0].Sequence); // stamped at open
    }

    [Test]
    public void AppendReasoning_AfterTool_OpensNewSegmentNotExtendingPriorOne()
    {
        var acc = new NodeChatPartAccumulator();

        acc.AppendReasoning("first segment", sequence: 0);
        acc.AppendToolRequested("call-1", "DoThing", null, false, sequence: 1);
        acc.AppendReasoning("second ", sequence: 2);
        acc.AppendReasoning("segment", sequence: 4); // extends the second segment

        var parts = acc.Snapshot();

        AssertEx.Equal(3, parts.Count);
        AssertEx.Equal("first segment", parts[0].Text);
        AssertEx.Equal("second segment", parts[2].Text);
        AssertEx.Equal(2, parts[2].Sequence); // stamped when 2nd segment opened
    }

    // ── (b) #6342 dup guard — duplicate Requested for same toolCallId ────────

    [Test]
    public void AppendToolRequested_DuplicateCallId_DoesNotAddSecondToolPart()
    {
        var acc = new NodeChatPartAccumulator();

        acc.AppendToolRequested("call-1", "GetCurrentTime", "{}", false, sequence: 0);
        acc.AppendToolRequested("call-1", "GetCurrentTime", "{}", false, sequence: 1); // duplicate

        var parts = acc.Snapshot();

        AssertEx.Equal(1, parts.Count);
        AssertEx.Equal("call-1", parts[0].ToolCallId);
        AssertEx.Equal(0, parts[0].Sequence); // first open wins
    }

    // ── (c) Completed collapses onto Requested by toolCallId ─────────────────

    [Test]
    public void CompleteToolCall_WhenPriorRequested_CollapsesSetsResultAndReceivedState()
    {
        var acc = new NodeChatPartAccumulator();

        acc.AppendToolRequested("call-1", "GetCurrentTime", "{\"tz\":\"UTC\"}", false, sequence: 0);
        acc.CompleteToolCall("call-1", "GetCurrentTime", "2026-06-01T00:00:00Z", isError: false, sequence: 1);

        var parts = acc.Snapshot();

        AssertEx.Equal(1, parts.Count);
        var tool = parts[0];
        AssertEx.Equal(NodeChatMessagePartKinds.Tool, tool.Kind);
        AssertEx.Equal("call-1", tool.ToolCallId);
        AssertEx.Equal(NodeChatToolPartStates.Received, tool.State);
        AssertEx.Equal("2026-06-01T00:00:00Z", tool.Result);
        AssertEx.Equal("{\"tz\":\"UTC\"}", tool.Args); // args preserved from requested phase
        AssertEx.Equal(0, tool.Sequence); // stamped at requested open, not completed
    }

    [Test]
    public void CompleteToolCall_WhenIsError_SetsFailedState()
    {
        var acc = new NodeChatPartAccumulator();

        acc.AppendToolRequested("call-err", "RunScript", "{}", true, sequence: 0);
        acc.CompleteToolCall("call-err", "RunScript", "permission denied", isError: true, sequence: 1);

        var parts = acc.Snapshot();

        AssertEx.Equal(1, parts.Count);
        AssertEx.Equal(NodeChatToolPartStates.Failed, parts[0].State);
        AssertEx.Equal("permission denied", parts[0].Result);
    }

    // ── (d) result-without-prior-call defensive add ──────────────────────────

    [Test]
    public void CompleteToolCall_WithoutPriorRequested_AddsDefensiveToolPart()
    {
        var acc = new NodeChatPartAccumulator();

        // Completed arrives with no prior Requested (defensive path at ~:92 in the accumulator)
        acc.CompleteToolCall("call-orphan", "SomeTool", "result text", isError: false, sequence: 5);

        var parts = acc.Snapshot();

        AssertEx.Equal(1, parts.Count);
        var tool = parts[0];
        AssertEx.Equal(NodeChatMessagePartKinds.Tool, tool.Kind);
        AssertEx.Equal("call-orphan", tool.ToolCallId);
        AssertEx.Equal("SomeTool", tool.Name);
        AssertEx.Equal(NodeChatToolPartStates.Received, tool.State);
        AssertEx.Equal("result text", tool.Result);
        AssertEx.Equal(5, tool.Sequence);
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
        AssertEx.Equal(1, parts.Count);
        AssertEx.Equal("second result", parts[0].Result);
    }

    // ── (e) Snapshot() sorts by Sequence despite insertion order ─────────────

    [Test]
    public void Snapshot_SortsBySequence_EvenWhenHigherSequenceToolAddedBeforeLowerSequenceReasoning()
    {
        var acc = new NodeChatPartAccumulator();

        // Simulate concurrent feed: tool part stamped at seq=1, then reasoning delta at seq=0
        // (lower sequence added after — positional insertion is out of order).
        acc.AppendToolRequested("call-1", "GetCurrentTime", null, false, sequence: 1);
        acc.AppendReasoning("pre-tool thinking", sequence: 0);

        var parts = acc.Snapshot();

        AssertEx.Equal(2, parts.Count);
        // Snapshot must reorder by sequence: seq=0 reasoning first, seq=1 tool second.
        AssertEx.Equal(NodeChatMessagePartKinds.Reasoning, parts[0].Kind);
        AssertEx.Equal(0, parts[0].Sequence);
        AssertEx.Equal(NodeChatMessagePartKinds.Tool, parts[1].Kind);
        AssertEx.Equal(1, parts[1].Sequence);
    }

    [Test]
    public void Snapshot_MultipleCallsReturnConsistentView()
    {
        var acc = new NodeChatPartAccumulator();
        acc.AppendReasoning("thinking", sequence: 0);
        acc.AppendToolRequested("call-1", "GetCurrentTime", null, false, sequence: 1);
        acc.CompleteToolCall("call-1", "GetCurrentTime", "now", isError: false, sequence: 2);

        var first = acc.Snapshot();
        var second = acc.Snapshot();

        // Both snapshots reflect the same state; they are independent immutable copies.
        AssertEx.Equal(first.Count, second.Count);
        AssertEx.Equal(first[0].Kind, second[0].Kind);
        AssertEx.Equal<string?>(first[1].State, second[1].State);
    }

    // ── HasParts ─────────────────────────────────────────────────────────────

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

        acc.AppendReasoning(null, sequence: 0);
        acc.AppendReasoning(string.Empty, sequence: 1);

        AssertEx.False(acc.HasParts);
        AssertEx.Equal(0, acc.Snapshot().Count);
    }
}
