// The streaming-update mapping must surface BOTH a reasoning delta and a visible-text delta when one source
// update carries both, in order (reasoning first) — previously an update with any reasoning returned early and dropped
// its visible text. The concrete MAF StreamingRun cannot be faked, so the pure mapping is exercised through the internal
// static ComposeStreamingUpdates seam that carries the logic (mirrors IdleStreamGuardTests' approach for this class).

namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation.Orchestration;

using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class OrchestrationRunSessionTests
{
    [Test]
    public async Task CompletePendingApprovalAsync_WhenSendFailsOnce_PreservesRequestForRetryAndOnlyRemovesAfterSuccess()
    {
        var pending = new ConcurrentDictionary<string, string>(StringComparer.Ordinal)
        {
            ["approval-1"] = "request"
        };
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => OrchestrationRunSession.CompletePendingApprovalAsync(pending,
            "approval-1",
            _ => ++attempts == 1 ? ValueTask.FromException(new InvalidOperationException("fail once")) : ValueTask.CompletedTask));

        AssertEx.True(pending.ContainsKey("approval-1"), "A failed send must leave the approval pending so the watchdog stays suspended and the caller can retry.");
        await OrchestrationRunSession.CompletePendingApprovalAsync(pending, "approval-1", _ => ValueTask.CompletedTask).ConfigureAwait(false);
        AssertEx.False(pending.ContainsKey("approval-1"), "The request is removed only after the workflow accepts the response.");
    }

    [Test]
    public async Task CompletePendingApprovalAsync_WhenWatchRestartsClockDuringGatedFailedSend_ReSuspendsAfterRestoringPendingRequest()
    {
        var pending = new ConcurrentDictionary<string, string>(StringComparer.Ordinal)
        {
            ["approval-1"] = "request"
        };
        using var idleClock = new CancellationTokenSource();
        var sendClaimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var completion = OrchestrationRunSession.CompletePendingApprovalAsync(pending,
            "approval-1",
            async _ =>
            {
                sendClaimed.SetResult();
                await releaseFailure.Task.ConfigureAwait(false);
                throw new InvalidOperationException("send failed");
            },
            () => idleClock.CancelAfter(Timeout.InfiniteTimeSpan));

        await sendClaimed.Task.ConfigureAwait(false);
        idleClock.CancelAfter(TimeSpan.FromMilliseconds(50)); // The watch observes the temporary empty dictionary.
        releaseFailure.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => completion);
        await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);

        AssertEx.True(pending.ContainsKey("approval-1"));
        AssertEx.False(idleClock.IsCancellationRequested,
            "Restoring a failed approval must re-suspend a clock the watch restarted while the request was claimed.");
    }

    [Test]
    public void ComposeStreamingUpdates_WhenReasoningAndText_EmitsBothReasoningFirst()
    {
        var contents = new List<AIContent>
        {
            new TextReasoningContent("thinking about it"),
            new TextContent("the visible answer")
        };

        var updates = OrchestrationRunSession.ComposeStreamingUpdates(contents, "the visible answer", "participant-key", "Specialist");

        AssertEx.Equal(expected: 2, updates.Count, "a single update carrying both reasoning and text must yield two deltas");
        AssertEx.Equal(OrchestrationUpdateKind.ReasoningDelta, updates[0].Kind);
        AssertEx.Equal("thinking about it", updates[0].Text);
        AssertEx.Equal(OrchestrationUpdateKind.TextDelta, updates[1].Kind);
        AssertEx.Equal("the visible answer", updates[1].Text);
        // Participant attribution rides both deltas.
        AssertEx.Equal("participant-key", updates[1].ParticipantKey);
        AssertEx.Equal("Specialist", updates[1].ParticipantName);
    }

    [Test]
    public void ComposeStreamingUpdates_WhenReasoningOnly_EmitsSingleReasoningDelta()
    {
        var contents = new List<AIContent>
        {
            new TextReasoningContent("just thinking")
        };

        var updates = OrchestrationRunSession.ComposeStreamingUpdates(contents, text: null, "key", "name");

        AssertEx.Equal(expected: 1, updates.Count);
        AssertEx.Equal(OrchestrationUpdateKind.ReasoningDelta, updates[0].Kind);
        AssertEx.Equal("just thinking", updates[0].Text);
    }

    [Test]
    public void ComposeStreamingUpdates_WhenTextOnly_EmitsSingleTextDelta()
    {
        var contents = new List<AIContent>
        {
            new TextContent("visible only")
        };

        var updates = OrchestrationRunSession.ComposeStreamingUpdates(contents, "visible only", "key", "name");

        AssertEx.Equal(expected: 1, updates.Count);
        AssertEx.Equal(OrchestrationUpdateKind.TextDelta, updates[0].Kind);
        AssertEx.Equal("visible only", updates[0].Text);
    }

    [Test]
    public void ComposeStreamingUpdates_WhenNeither_EmitsNothing()
    {
        // A handoff FunctionCallContent carries no user-visible content and no reasoning → no delta surfaces.
        var contents = new List<AIContent>
        {
            new FunctionCallContent("call-1", "handoff_to_specialist")
        };

        var updates = OrchestrationRunSession.ComposeStreamingUpdates(contents, text: null, participantKey: null, participantName: null);

        AssertEx.Empty(updates);
    }
}
