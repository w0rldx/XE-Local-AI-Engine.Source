namespace XE_Local_AI_Engine.Tests.Events;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The content offset on a requested tool call splits interim narration off the answer; the real
///     <see cref="WorkerEventDispatcher" /> stamps it under the lock that orders the content appends.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class WorkerEventDispatcherToolContentOffsetTests
{
    private const string Interim = "Let me search for that. ";

    [Test]
    public async Task RequestedToolCall_CarriesTheStreamedContentLength_AndTheCompletedPhaseCarriesNone()
    {
        var dispatcher = NewDispatcher();
        var invocationId = Guid.NewGuid();
        var payloads = new List<ToolCallLifecyclePayload>();
        dispatcher.ToolCallLifecycleChanged += (_, args) => payloads.Add(args.Payload);

        await using var lease = await dispatcher.ReportInvocationAssignedAsync(Package(invocationId));
        await dispatcher.ReportInvocationStreamChunkAsync(invocationId, "Let me ");
        await dispatcher.ReportInvocationStreamChunkAsync(invocationId, "search for that. ");
        await dispatcher.ReportToolCallLifecycleAsync(Lifecycle(invocationId, ToolCallLifecyclePhase.Requested));
        await dispatcher.ReportToolCallLifecycleAsync(Lifecycle(invocationId, ToolCallLifecyclePhase.Completed));

        AssertEx.Equal(expected: 2, payloads.Count);
        AssertEx.Equal(Interim.Length, payloads[0].ContentOffset);
        AssertEx.Null(payloads[1].ContentOffset);

        var requested = ChatStreamEventMapper.ToolCallEvent(Guid.NewGuid(), invocationId, invocationId, payloads[0], timestampMs: 0, sequence: 0);
        AssertEx.Equal((long)Interim.Length, requested.ContentOffset, "The wire event carries the offset the client splits at.");
    }

    [Test]
    public async Task ResumeReplay_CarriesTheToolOffset_BeforeAFullTextSnapshot_SoTheClientRebuildsTheSameSplit()
    {
        var dispatcher = NewDispatcher();
        var registry = new InvocationResumeRegistry(dispatcher, TimeProvider.System, NullLogger<InvocationResumeRegistry>.Instance);
        var invocationId = Guid.NewGuid();

        await using var lease = await dispatcher.ReportInvocationAssignedAsync(Package(invocationId));
        await dispatcher.ReportInvocationStreamChunkAsync(invocationId, Interim);
        await dispatcher.ReportToolCallLifecycleAsync(Lifecycle(invocationId, ToolCallLifecyclePhase.Requested));
        await dispatcher.ReportToolCallLifecycleAsync(Lifecycle(invocationId, ToolCallLifecyclePhase.Completed));
        await dispatcher.ReportInvocationStreamChunkAsync(invocationId, "The answer");

        var events = new List<ChatStreamEvent>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var consumer = Task.Run(async () =>
        {
            await foreach (var streamEvent in registry.ResumeAsync(invocationId, cancellation.Token))
            {
                events.Add(streamEvent);
            }
        }, cancellation.Token);

        await AssertEx.EventuallyAsync(() => events.Any(evt => evt.Type == ChatStreamEventTypes.AssistantSnapshot), TimeSpan.FromSeconds(10));
        await dispatcher.ReportInvocationCompletedAsync(invocationId);
        await consumer;

        var requestedIndex = events.FindIndex(evt => evt.Type == ChatStreamEventTypes.ToolCallRequested);
        var snapshotIndex = events.FindIndex(evt => evt.Type == ChatStreamEventTypes.AssistantSnapshot);
        AssertEx.True(requestedIndex >= 0 && requestedIndex < snapshotIndex, "The tool timeline replays before the snapshot.");
        AssertEx.Equal((long)Interim.Length, events[requestedIndex].ContentOffset);
        // The snapshot keeps the GLOBAL text, so its offset space matches the deltas and the split offset above.
        AssertEx.Equal(Interim + "The answer", events[snapshotIndex].Content);
        AssertEx.Equal((long)(Interim + "The answer").Length, events[snapshotIndex].ContentOffset);
    }

    private static WorkerEventDispatcher NewDispatcher()
    {
        return new WorkerEventDispatcher(Substitute.For<IInvocationRunner>(),
            Substitute.For<IInvocationHistory>(),
            NullLogger<WorkerEventDispatcher>.Instance,
            TimeProvider.System);
    }

    private static RuntimePackage Package(Guid invocationId)
    {
        return RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithConversationId(Guid.NewGuid()).Build();
    }

    private static ToolCallLifecyclePayload Lifecycle(Guid invocationId, ToolCallLifecyclePhase phase)
    {
        return new ToolCallLifecyclePayload
        {
            InvocationId = invocationId,
            ToolCallId = "call-1",
            ToolName = "web_search",
            Phase = phase,
            Arguments = phase == ToolCallLifecyclePhase.Requested ? "{}" : null,
            Result = phase == ToolCallLifecyclePhase.Completed ? "results" : null
        };
    }
}
