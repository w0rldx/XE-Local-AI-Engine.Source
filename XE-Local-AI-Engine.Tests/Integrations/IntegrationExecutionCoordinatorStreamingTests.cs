namespace XE_Local_AI_Engine.Tests.Integrations;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;
using Harness = IntegrationCoordinatorHarness;

/// <summary>The writer's options, spelled out once so the reaper test reads as one thought.</summary>
internal sealed class IntegrationSseWriterOptions : IOptions<IntegrationOptions>, IDisposable
{
    public IntegrationOptions Value { get; } = new();

    public void Dispose()
    {
    }
}

/// <summary>
///     The stream mapper as the coordinator actually drives it: hung on the ONE subscription the coordinator opens
///     before the lease, drained before the terminal transaction, and detached with that subscription afterwards.
///     <para>
///         Everything here is about ORDER and OWNERSHIP. The terminal event must be the highest sequence in the ring,
///         which is only true because the drain is awaited first; and no <c>execution.*</c> row may come from the
///         mapper, because a second producer of a terminal is a second answer to "did this run finish".
///     </para>
/// </summary>
public sealed class IntegrationExecutionCoordinatorStreamingTests
{
    /// <summary>The end-to-end shape: one ring, one persisted subset, the terminal last.</summary>
    [Test]
    public async Task Run_StreamsDeltasAndToolEventsAndLeavesTheTerminalAsTheHighestSequence()
    {
        using var harness = new Harness();
        var executionId = harness.SeedLive();
        harness.DuringRun = static (h, package) =>
        {
            RaiseContent(h, package.InvocationId, "Three primes:", InvocationStatus.Running);
            RaiseTool(h, package.InvocationId, ToolCallLifecyclePhase.Requested);
            RaiseTool(h, package.InvocationId, ToolCallLifecyclePhase.Completed);
        };

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var streamed = await ReadAsync(harness, executionId);
        AssertEx.True(streamed.Select(static streamEvent => streamEvent.Type)
                              .SequenceEqual([
                                  IntegrationStreamEventTypes.ExecutionStarted,
                                  IntegrationStreamEventTypes.AssistantDelta,
                                  IntegrationStreamEventTypes.ToolStarted,
                                  IntegrationStreamEventTypes.ToolCompleted,
                                  IntegrationStreamEventTypes.AssistantCompleted,
                                  IntegrationStreamEventTypes.ExecutionCompleted
                              ]),
            "The mapper's events sit between the coordinator's phase events, and the terminal closes the ring.");
        AssertEx.Equal(streamed[^1].Sequence,
            harness.Buffer.LastSequence(executionId),
            "The drain latches the handlers shut, so nothing can mint a sequence above the terminal.");

        var persisted = harness.Executions.Events.Where(row => row.ExecutionId == executionId).ToArray();
        AssertEx.True(persisted.Select(static row => row.EventType)
                               .SequenceEqual([
                                   IntegrationStreamEventTypes.ExecutionStarted,
                                   IntegrationStreamEventTypes.ToolStarted,
                                   IntegrationStreamEventTypes.ToolCompleted,
                                   IntegrationStreamEventTypes.ExecutionCompleted
                               ]),
            "tool.* rows are the ONLY rows the mapper writes, and neither assistant type is ever persisted.");
    }

    /// <summary>Test 19 — the mapper is detached with the coordinator's own subscription.</summary>
    [Test]
    public async Task Run_AfterTheCoordinatorUnsubscribed_AppendsNothingMore()
    {
        using var harness = new Harness();
        var executionId = harness.SeedLive();
        var invocationId = Guid.Empty;
        harness.DuringRun = (h, package) =>
        {
            invocationId = package.InvocationId;
            RaiseContent(h, package.InvocationId, "answer", InvocationStatus.Running);
        };

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);
        var head = harness.Buffer.LastSequence(executionId);

        RaiseContent(harness, invocationId, "answer with a late tail", InvocationStatus.Running);
        RaiseTool(harness, invocationId, ToolCallLifecyclePhase.Requested);

        AssertEx.Equal(head, harness.Buffer.LastSequence(executionId), "S2 attaches to S1's lifetime and never opens its own, so a late raise reaches nothing.");
    }

    /// <summary>Test 22, at the coordinator: a drain failure is the run's failure.</summary>
    [Test]
    public async Task Run_WhenTheToolEventCannotBePersisted_TerminalizesInternalFailure()
    {
        using var harness = new Harness();
        var executionId = harness.SeedLive();
        // Only the mapper's rows fail: the coordinator's own execution.started must still be written, or the run would
        // never reach the drain this test is about.
        harness.Executions.FailAppendEventWhen = static append => append.EventType.StartsWith("tool.", StringComparison.Ordinal);
        harness.DuringRun = static (h, package) => RaiseTool(h, package.InvocationId, ToolCallLifecyclePhase.Requested);

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationExecutionStatus.Failed, row.Status, "An incomplete transcript is not a completed run, whatever the model produced.");
        AssertEx.Equal(IntegrationFailureCategories.InternalFailure, row.FailureCategory);

        var streamed = await ReadAsync(harness, executionId);
        AssertEx.Equal(IntegrationStreamEventTypes.ExecutionFailed, streamed[^1].Type);
        AssertEx.Equal(expected: 1,
            streamed.Count(static streamEvent => streamEvent.Type.StartsWith("execution.completed", StringComparison.Ordinal)
                                                 || streamEvent.Type.StartsWith("execution.failed", StringComparison.Ordinal)
                                                 || streamEvent.Type.StartsWith("execution.cancelled", StringComparison.Ordinal)),
            "One terminal, from one producer, even when the drain is what failed.");
    }

    /// <summary>
    ///     §9(b) as a NEGATIVE requirement: neither the coordinator nor the SSE writer registers an attachment handle,
    ///     so the reaper cannot see an integration run at all. If either ever attached, an integrator that closed its
    ///     stream to poll instead would have its run cancelled 300 s later — the exact failure the brief forbids.
    /// </summary>
    [Test]
    public async Task DetachedInvocationReaper_NeverSeesAnIntegrationRun_EvenAfterAStreamCameAndWent()
    {
        using var harness = new Harness();
        var executionId = harness.SeedLive();
        var invocationId = Guid.Empty;
        harness.DuringRun = (h, package) =>
        {
            invocationId = package.InvocationId;
            RaiseContent(h, package.InvocationId, "answer", InvocationStatus.Running);
        };

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        // An SSE consumer attaches and goes away, which is what a caller that switches to polling actually does.
        using var options = new IntegrationSseWriterOptions();
        using var writer = new IntegrationSseWriter(harness.Buffer, options, TimeProvider.System, NullLogger<IntegrationSseWriter>.Instance);
        var context = new DefaultHttpContext
        {
            Response =
            {
                Body = new MemoryStream()
            }
        };
        _ = await writer.WriteAsync(context, executionId, sinceSequence: 0, context.RequestAborted);

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var tracker = new InvocationAttachmentTracker(new Lazy<IWorkerEventDispatcher>(() => dispatcher), TimeProvider.System);
        var runner = Substitute.For<IInvocationRunner>();
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();
        _ = runtimeSettings.GetDetachedGraceSecondsAsync(Arg.Any<CancellationToken>()).Returns(1);
        var reaper = new DetachedInvocationReaper(tracker, runner, runtimeSettings, TimeProvider.System, NullLogger<DetachedInvocationReaper>.Instance);

        AssertEx.Empty(tracker.ListDetached(), "An entry exists only after Attach, and nothing on this path ever calls it.");
        await reaper.ReapAsync(CancellationToken.None);

        runner.DidNotReceive().CancelDetached(Arg.Any<Guid>());
        runner.DidNotReceive().CancelDetached(invocationId);
        reaper.Dispose();
    }

    /// <summary>
    ///     Live F1 — a failed run's terminal frame carries <c>{category, summary}</c>, and the persisted row carries the
    ///     same bytes. A null payload told an integrator nothing about why the run ended, and the reason lived only in a
    ///     column no external route returns.
    /// </summary>
    [Test]
    public async Task Run_WhenTheRunFails_PublishesTheReasonAndPersistsTheSameDetail()
    {
        using var harness = new Harness();
        harness.TerminalStatus = InvocationStatus.Failed;
        harness.TerminalFailureCategory = FailureCategory.ProviderUnreachable;
        harness.TerminalError = "the provider could not be reached";
        var executionId = harness.SeedLive();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var terminal = (await ReadAsync(harness, executionId))[^1];
        AssertEx.Equal(IntegrationStreamEventTypes.ExecutionFailed, terminal.Type);
        var payload = terminal.Payload ?? throw new AssertionException("The terminal frame carries no payload, so the caller learns nothing about the failure.");
        AssertEx.Equal(IntegrationFailureCategories.InternalFailure, payload.GetProperty("category").GetString(), "Only the closed categories reach the wire.");
        AssertEx.NotEmpty(payload.GetProperty("summary").GetString(), "A category with no summary is half an answer.");
        AssertEx.Equal(payload.GetRawText(),
            harness.Executions.Events.Last(row => row.ExecutionId == executionId).DetailJson,
            "The poll route reads this row, so it must hand back exactly the envelope the stream gave.");
    }

    /// <summary>Live F1 — a completed run's terminal frame carries <c>{tokens?, durationMs}</c>.</summary>
    [Test]
    public async Task Run_WhenTheRunCompletes_PublishesTheDurationAndTheTokenTotal()
    {
        using var harness = new Harness();
        harness.TerminalTotalTokens = 1_234;
        var executionId = harness.SeedLive();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var terminal = (await ReadAsync(harness, executionId))[^1];
        AssertEx.Equal(IntegrationStreamEventTypes.ExecutionCompleted, terminal.Type);
        var payload = terminal.Payload ?? throw new AssertionException("The terminal frame carries no payload, so a caller cannot tell a fast run from a slow one.");
        AssertEx.Equal(expected: 12L, payload.GetProperty("durationMs").GetInt64(), "The RUN's own duration, not the wall time since the request was admitted.");
        AssertEx.Equal(expected: 1_234, payload.GetProperty("tokens").GetInt32());
        AssertEx.Equal(payload.GetRawText(), harness.Executions.Events.Last(row => row.ExecutionId == executionId).DetailJson);
    }

    /// <summary>Live F1 — `tokens?` is optional: a provider that reported none omits the field rather than sending null.</summary>
    [Test]
    public async Task Run_WhenNoTokenTotalWasReported_OmitsTheTokensField()
    {
        using var harness = new Harness();
        var executionId = harness.SeedLive();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var payload = (await ReadAsync(harness, executionId))[^1].Payload ?? throw new AssertionException("The terminal frame carries no payload.");
        AssertEx.False(payload.TryGetProperty("tokens", out _), "A null tokens field would make every caller special-case it.");
        AssertEx.True(payload.TryGetProperty("durationMs", out _), "durationMs is not optional.");
    }

    private static void RaiseContent(Harness harness, Guid invocationId, string content, InvocationStatus status) =>
        harness.Dispatcher.InvocationStateChanged += Raise.EventWith(new InvocationStateChangedEventArgs(new InvocationState
        {
            InvocationId = invocationId,
            Status = status,
            StreamedContent = content
        }));

    private static void RaiseTool(Harness harness, Guid invocationId, ToolCallLifecyclePhase phase) =>
        harness.Dispatcher.ToolCallLifecycleChanged += Raise.EventWith(new ToolCallLifecycleChangedEventArgs(new ToolCallLifecyclePayload
        {
            InvocationId = invocationId,
            ToolCallId = "call-1",
            ToolName = "read_file",
            Phase = phase
        }));

    /// <summary>
    ///     The whole ring for one execution, from sequence 1. The run has terminalized by the time this is called, so
    ///     the reader ends on its own; the ceiling turns a regression into a failure rather than a hang.
    /// </summary>
    private static async Task<IReadOnlyList<IntegrationStreamEvent>> ReadAsync(Harness harness, Guid executionId)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new List<IntegrationStreamEvent>();
        await foreach (var streamEvent in harness.Buffer.ReadAsync(executionId, sinceSequence: 1, cancellation.Token).ConfigureAwait(false))
        {
            events.Add(streamEvent);
        }

        return events;
    }
}
