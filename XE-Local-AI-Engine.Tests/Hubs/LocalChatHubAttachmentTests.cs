namespace XE_Local_AI_Engine.Tests.Hubs;

using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The attach/detach seam, which unit tests of the tracker and the reaper structurally cannot see: they drive
///     Attach and Dispose directly, so they pass no matter when the hub actually releases.
///     <para>
///         <c>NodeChatStreamService.SendMessageCoreAsync</c>
///         awaits its pump and runner tasks in the <c>finally</c> AFTER its SSE loop exits, so the enumerable the hub
///         wraps does not complete until the whole run is over — often minutes after the browser has gone. Releasing the
///         attachment from the wrapper's own <c>finally</c> therefore recorded the detach only once the run had already
///         terminalized, so the reaper never once saw a detached run and the disconnect grace was silently dead in
///         production while every unit test stayed green.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LocalChatHubAttachmentTests
{
    [Test]
    public async Task SendMessage_WhenTheClientDisconnects_RecordsTheDetachWhileTheRunIsStillGoing()
    {
        var invocationId = Guid.NewGuid();
        var tracker = CreateTracker();
        var runFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamFaultedOnDisconnect = false;
        using var clientGone = new CancellationTokenSource();

        // Mirrors the real service: the SSE loop ends on the client token, then the iterator BLOCKS in its finally
        // draining the run before it returns. If the release waits for the enumerable, it waits for runFinished.
        var streamService = Substitute.For<INodeChatStreamService>();
        streamService.SendMessageAsync(Arg.Any<NodeChatStreamRequest>(), Arg.Any<CancellationToken>())
                     .Returns(_ => RunThatOutlivesTheClient(invocationId, runFinished.Task));

        using var hub = CreateHub(streamService, tracker);
        var pump = Task.Run(async () =>
        {
            // A cancelled SignalR stream faults its consumer; that is the disconnect, not a test failure.
            try
            {
                await foreach (var _ in hub.SendMessage(new NodeChatStreamRequest(Guid.NewGuid(), "hi"), clientGone.Token))
                {
                    firstDelivered.TrySetResult();
                }
            }
            catch (OperationCanceledException)
            {
                streamFaultedOnDisconnect = true;
            }
        });

        // The hub attaches on the first event it forwards, so a delivered event proves the attachment exists.
        await firstDelivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        AssertEx.False(tracker.IsDetached(invocationId), "an actively streaming client is attached, not detached");

        await clientGone.CancelAsync();

        // The run has NOT finished. The detach must already be visible to the reaper.
        await AssertEx.EventuallyAsync(() => tracker.IsDetached(invocationId), TimeSpan.FromSeconds(5),
            "a disconnected client must be recorded as detached immediately, not when the run finally ends");
        AssertEx.False(runFinished.Task.IsCompleted, "the run is deliberately still going — that is the whole point");

        // Disposing the source waits for the run, so the hub's own finally (and its Forget) cannot have run yet: the reaper sees the entry.
        await AssertEx.StaysIncompleteAsync(pump, "the hub stream only ends once the source, and with it the run, has ended");
        AssertEx.ContainsSingle(tracker.ListDetached(), entry => entry.InvocationId == invocationId, "a disconnected, still-running turn is reapable");

        runFinished.SetResult();
        await pump.WaitAsync(TimeSpan.FromSeconds(10));
        AssertEx.True(streamFaultedOnDisconnect, "the disconnect ends the client's stream");

        // This fake publishes no terminal, like a turn cancelled while queued: the entry must still go once the run is over,
        // or it sits in ListDetached and the detached gauge for the process lifetime.
        AssertEx.False(tracker.IsDetached(invocationId), "the send stream returning means the run is over, so its entry is dropped");
        AssertEx.Equal(expected: 0, tracker.ListDetached().Count);
    }

    [Test]
    public async Task ResumeMessage_WhenTheStreamEndsNormally_ReleasesExactlyOnce()
    {
        // The idempotent-handle guarantee: the token callback and the finally can both fire, and a double release would
        // report an attached run as abandoned. A resume ends while the run goes on, so its entry survives to be counted.
        var invocationId = Guid.NewGuid();
        var tracker = CreateTracker();
        using var clientGone = new CancellationTokenSource();

        var resumeRegistry = Substitute.For<IInvocationResumeRegistry>();
        resumeRegistry.ResumeAsync(invocationId, Arg.Any<CancellationToken>())
                      .Returns(_ => OneEventThenEnd(invocationId));

        using var hub = CreateHub(Substitute.For<INodeChatStreamService>(), tracker, resumeRegistry);
        var delivered = 0;
        await foreach (var _ in hub.ResumeMessage(invocationId, clientGone.Token))
        {
            delivered++;
        }

        AssertEx.Equal(expected: 1, delivered);
        AssertEx.True(tracker.IsDetached(invocationId));

        // Cancelling afterwards must not double-release: a second release would make the entry attachable-negative.
        await clientGone.CancelAsync();
        AssertEx.Equal(expected: 1, tracker.ListDetached().Count);

        using var reattached = tracker.Attach(invocationId);
        AssertEx.False(tracker.IsDetached(invocationId), "one attach must be enough to clear the detachment");
    }

    [Test]
    public void ResumeMessage_ForAnInvocationThatIsNotLive_ThrowsAHubException()
    {
        var invocationId = Guid.NewGuid();
        var resumeRegistry = Substitute.For<IInvocationResumeRegistry>();
        resumeRegistry.ResumeAsync(invocationId, Arg.Any<CancellationToken>())
                      .Returns<IAsyncEnumerable<ChatStreamEvent>>(_ => throw new InvalidOperationException($"Invocation {invocationId} is not resumable."));
        using var hub = CreateHub(Substitute.For<INodeChatStreamService>(), CreateTracker(), resumeRegistry);

        var exception = AssertEx.Throws<HubException>(() => hub.ResumeMessage(invocationId, CancellationToken.None));

        AssertEx.Contains(exception.Message, "not resumable");
    }

    [Test]
    public async Task ResumeConversation_WhenTheTurnEndsBetweenLookupAndResume_ReturnsAnEmptyStream()
    {
        var conversationId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var resumeRegistry = Substitute.For<IInvocationResumeRegistry>();
        resumeRegistry.TryGetLiveInvocationIdForConversation(conversationId).Returns(invocationId);
        resumeRegistry.ResumeAsync(invocationId, Arg.Any<CancellationToken>())
                      .Returns<IAsyncEnumerable<ChatStreamEvent>>(_ => throw new InvalidOperationException("already terminal"));
        using var hub = CreateHub(Substitute.For<INodeChatStreamService>(), CreateTracker(), resumeRegistry);

        var delivered = 0;
        await foreach (var _ in hub.ResumeConversation(conversationId, CancellationToken.None))
        {
            delivered++;
        }

        AssertEx.Equal(expected: 0, delivered, "nothing is live, which is the documented empty-stream answer.");
    }

    private static LocalChatHub CreateHub(INodeChatStreamService streamService,
        IInvocationAttachmentTracker tracker,
        IInvocationResumeRegistry? resumeRegistry = null)
    {
        return new LocalChatHub(streamService,
            Substitute.For<INodeChatRegenerationService>(),
            resumeRegistry ?? Substitute.For<IInvocationResumeRegistry>(),
            tracker,
            Options.Create(new SecurityOptions()));
    }

    private static InvocationAttachmentTracker CreateTracker()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        return new InvocationAttachmentTracker(new Lazy<IWorkerEventDispatcher>(() => dispatcher), TimeProvider.System);
    }

    // One event, then the client token ends the forwarding loop while the iterator keeps holding the run open —
    // the exact shape of SendMessageCoreAsync's finally awaiting pumpTask/runTask.
    private static async IAsyncEnumerable<ChatStreamEvent> RunThatOutlivesTheClient(Guid invocationId,
        Task runFinished,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        try
        {
            yield return NewEvent(invocationId);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            await runFinished;
        }
    }

    private static async IAsyncEnumerable<ChatStreamEvent> OneEventThenEnd(Guid invocationId)
    {
        yield return NewEvent(invocationId);
        await Task.Yield();
    }

    private static ChatStreamEvent NewEvent(Guid invocationId)
    {
        return new ChatStreamEvent
        {
            Type = ChatStreamEventTypes.AssistantQueued,
            ConversationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            RequestId = invocationId,
            Status = "queued",
            Sequence = 0,
            OccurredAtUtc = 0
        };
    }
}
