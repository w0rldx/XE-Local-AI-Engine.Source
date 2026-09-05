namespace XE_Local_AI_Engine.Tests.Events;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using NSec.Cryptography;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class WorkerEventDispatcherTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    [Test]
    public void CurrentInvocation_Initially_IsNull()
    {
        var dispatcher = CreateDispatcher(Substitute.For<IInvocationRunner>());

        AssertEx.Null(dispatcher.CurrentInvocation);
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_CallsRunnerRunAsync()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);
        var package = RuntimePackageBuilder.Valid().Build();

        await dispatcher.DispatchInvocationAssignedAsync(CreateEncryptedPackage(package));

        await runner.Received(1).RunAsync(Arg.Is<InvocationExecutionContext>(context => context.Package.InvocationId == package.InvocationId
                                                                                        && context.Package.ConversationId == package.ConversationId
                                                                                        && context.Package.ClientNodeId == package.ClientNodeId
                                                                                        && context.MessageId != Guid.Empty
                                                                                        && context.EpochVersion == 1
                                                                                        && context.EpochKey.Length == 32),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DispatchInvocationAssignedV2Async_WhenPlainSync_RunsRunnerWithPlainContextAndSkipsEncryptedAssembly()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var assembler = Substitute.For<IRuntimePackageEnvelopeAssembler>();
#pragma warning disable CA2000 // The dispatcher retains this test registry; it has no disposal contract, and its isolated NSec key lives only for this test process.
        var nodeKeyRegistry = new FakeNodeKeyRegistry();
#pragma warning restore CA2000
        var dispatcher = CreateDispatcher(runner, assembler, new MockHubMessageSender(), nodeKeyRegistry);
        var package = RuntimePackageBuilder.Valid().Build();

        await dispatcher.DispatchInvocationAssignedV2Async(new InvocationAssignedEnvelope
        {
            StorageMode = "PlainSync",
            Plain = package,
            Encrypted = null
        });

        var current = AssertEx.NotNull(dispatcher.CurrentInvocation);
        AssertEx.Equal(package.InvocationId, current.InvocationId);
        assembler.DidNotReceiveWithAnyArgs().Assemble(default!);
        await runner.Received(1).RunAsync(Arg.Is<InvocationExecutionContext>(context => context.Package.InvocationId == package.InvocationId
                                                                                        && context.Package.ConversationId == package.ConversationId
                                                                                        && !context.IsEncrypted
                                                                                        && context.EpochKey.IsEmpty
                                                                                        && context.MessageId == Guid.Empty),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_WhenAlreadyBusy_QueuesSecondAssignment()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build();
        var second = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(call =>
              {
                  if (((InvocationExecutionContext)call[0]).Package.InvocationId == first.InvocationId)
                  {
                      firstEntered.TrySetResult();
                      return gate.Task;
                  }

                  return Task.CompletedTask;
              });

        var dispatcher = CreateDispatcher(runner);

        var firstDispatch = dispatcher.DispatchInvocationAssignedAsync(CreateEncryptedPackage(first));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondDispatch = dispatcher.DispatchInvocationAssignedAsync(CreateEncryptedPackage(second));

        await runner.Received(1).RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>());
        AssertEx.Equal(first.InvocationId, dispatcher.CurrentInvocation?.InvocationId ?? Guid.Empty);

        gate.SetResult();
        await Task.WhenAll(firstDispatch, secondDispatch);

        await runner.Received(1).RunAsync(Arg.Is<InvocationExecutionContext>(context => context.Package.InvocationId == first.InvocationId), Arg.Any<CancellationToken>());
        await runner.Received(1).RunAsync(Arg.Is<InvocationExecutionContext>(context => context.Package.InvocationId == second.InvocationId), Arg.Any<CancellationToken>());
        AssertEx.Equal(second.InvocationId, dispatcher.CurrentInvocation?.InvocationId ?? Guid.Empty);
    }

    [Test]
    public async Task ReportInvocationAssignedAsync_WhenSlotFree_SetsCurrentInvocationAndReturnsLease()
    {
        var dispatcher = CreateDispatcher(Substitute.For<IInvocationRunner>());
        var package = RuntimePackageBuilder.Valid().Build();

        var lease = await dispatcher.ReportInvocationAssignedAsync(package);

        AssertEx.Equal(package.InvocationId, dispatcher.CurrentInvocation?.InvocationId ?? Guid.Empty);
        await lease.DisposeAsync();
    }

    [Test]
    public async Task ReportApprovalLifecycleAsync_RecordsSessionScopeEligibilityOnThePendingApproval()
    {
        // The reconnect replay rebuilds the approval from this slot. ApprovalRequestPayload (the platform-hub contract
        // that seeds it) carries no session-scope field, so the runner's answer has to be folded on from the lifecycle
        // event — otherwise a reload drops it and the card falls back to the tool catalog.
        var dispatcher = CreateDispatcher(Substitute.For<IInvocationRunner>());
        var package = RuntimePackageBuilder.Valid().Build();
        var lease = await dispatcher.ReportInvocationAssignedAsync(package);

        await dispatcher.ReportApprovalRequestedAsync(new ApprovalRequestPayload
        {
            InvocationId = package.InvocationId,
            RequestId = "approval-1",
            Description = "A tool call requires approval before it runs."
        });
        await dispatcher.ReportApprovalLifecycleAsync(new ApprovalLifecyclePayload
        {
            InvocationId = package.InvocationId,
            RequestId = "approval-1",
            CallId = "call-7",
            ToolName = "run_skill_script",
            Description = "A tool call requires approval before it runs.",
            SessionScopeEligible = false
        });

        var pending = AssertEx.NotNull(dispatcher.CurrentInvocation?.PendingApproval);
        AssertEx.Equal(expected: false, pending.SessionScopeEligible);
        await lease.DisposeAsync();
    }

    [Test]
    public async Task ReportInvocationAssignedAsync_WhenRemoteRunning_QueuesUntilRemoteSlotReleased()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var remoteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build();
        var local = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build();
        var remoteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(_ =>
              {
                  remoteEntered.TrySetResult();
                  return remoteGate.Task;
              });

        var dispatcher = CreateDispatcher(runner);

        // Remote invocation starts and holds the shared slot.
        var remoteDispatch = dispatcher.DispatchInvocationAssignedAsync(CreateEncryptedPackage(remote));
        await remoteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Local assignment must QUEUE (not throw) while the remote holds the slot: the returned task cannot complete
        // until the remote releases the slot, so it is observably incomplete with no wall-clock wait.
        var localAssign = dispatcher.ReportInvocationAssignedAsync(local);
        AssertEx.False(localAssign.IsCompleted);
        AssertEx.Equal(remote.InvocationId, dispatcher.CurrentInvocation?.InvocationId ?? Guid.Empty);

        // Releasing the remote lets the queued local assignment proceed.
        remoteGate.SetResult();
        await remoteDispatch;
        var lease = await localAssign;

        AssertEx.Equal(local.InvocationId, dispatcher.CurrentInvocation?.InvocationId ?? Guid.Empty);
        await lease.DisposeAsync();
    }

    [Test]
    public async Task ReportInvocationAssignedAsync_WhileRemoteHoldsCollisionQueue_BlocksUntilRemoteReleasesThenProceeds()
    {
        // Deterministic collision-queue test: the remote run is gated on a TaskCompletionSource the test owns, and
        // we synchronize on the runner actually entering (remoteEntered) before starting the local turn, so there
        // is no sleep-before-dispatch race. The local turn's blocked/unblocked transition is observed via the
        // returned ReportInvocationAssignedAsync task plus CurrentInvocation ordering, not timing.
        var runner = Substitute.For<IInvocationRunner>();
        var remoteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRemote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build();
        var local = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(_ =>
              {
                  remoteEntered.TrySetResult();
                  return releaseRemote.Task;
              });

        var dispatcher = CreateDispatcher(runner);

        // Remote invocation starts and holds the shared _remoteInvocationQueue lease.
        var remoteDispatch = dispatcher.DispatchInvocationAssignedAsync(CreateEncryptedPackage(remote));
        await remoteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The local send must WAIT on the same gate before claiming the slot: its task does not complete and the
        // current invocation stays the remote one.
        var localAssign = dispatcher.ReportInvocationAssignedAsync(local);

        // The local send queues behind the remote lease: its task cannot complete until the remote releases, so it is
        // observably incomplete without a wall-clock race window.
        AssertEx.False(localAssign.IsCompleted, "The local send must queue behind the remote invocation and not acquire the slot while the remote holds it.");
        AssertEx.Equal(remote.InvocationId, dispatcher.CurrentInvocation?.InvocationId ?? Guid.Empty);

        // No spurious failed/persisted noise while queued: the local turn never reached the runner and the tracked
        // invocation is still the running remote one (not a failed local one).
        await runner.Received(1).RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>());
        AssertEx.NotEqual(InvocationStatus.Failed, dispatcher.CurrentInvocation?.Status ?? InvocationStatus.Failed);

        // Releasing the remote lease lets the queued local turn acquire the slot and become current.
        releaseRemote.SetResult();
        await remoteDispatch;
        var lease = await localAssign.WaitAsync(TimeSpan.FromSeconds(5));

        AssertEx.Equal(local.InvocationId, dispatcher.CurrentInvocation?.InvocationId ?? Guid.Empty);
        // The local turn does not drive the agent runner, so no additional RunAsync call was made for it.
        await runner.Received(1).RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>());
        await lease.DisposeAsync();
    }

    [Test]
    public async Task ReportInvocationAssignedAsync_WhenCancelledWhileQueued_AbortsWaitWithoutAssigning()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var remoteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build();
        var local = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build();
        var remoteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(_ =>
              {
                  remoteEntered.TrySetResult();
                  return remoteGate.Task;
              });

        var dispatcher = CreateDispatcher(runner);
        var remoteDispatch = dispatcher.DispatchInvocationAssignedAsync(CreateEncryptedPackage(remote));
        await remoteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The local assignment queues behind the remote lease; cancelling its token must abort the queued wait. The
        // wait honours the token whenever the cancel lands, so no delay is needed to "arm" the wait first.
        using var localCancellation = new CancellationTokenSource();
        var localAssign = dispatcher.ReportInvocationAssignedAsync(local, localCancellation.Token);

        await localCancellation.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => localAssign);
        AssertEx.Equal(remote.InvocationId, dispatcher.CurrentInvocation?.InvocationId ?? Guid.Empty);

        remoteGate.SetResult();
        await remoteDispatch;
    }

    [Test]
    public async Task DispatchInvocationAssignedV2Async_WhenStopAcceptingRemoteInvocations_IgnoresNewAssignment()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);
        var package = RuntimePackageBuilder.Valid().Build();

        dispatcher.StopAcceptingRemoteInvocations();

        await dispatcher.DispatchInvocationAssignedV2Async(new InvocationAssignedEnvelope
        {
            StorageMode = "PlainSync",
            Plain = package,
            Encrypted = null
        });

        AssertEx.False(dispatcher.IsAcceptingRemoteInvocations);
        AssertEx.Null(dispatcher.CurrentInvocation);
        await runner.DidNotReceive().RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StopAcceptingRemoteInvocations_DoesNotCancelAlreadyActiveAssignment()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runnerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(_ =>
              {
                  runnerEntered.TrySetResult();
                  return gate.Task;
              });
        var dispatcher = CreateDispatcher(runner);
        var package = RuntimePackageBuilder.Valid().Build();

        var dispatch = dispatcher.DispatchInvocationAssignedV2Async(new InvocationAssignedEnvelope
        {
            StorageMode = "PlainSync",
            Plain = package,
            Encrypted = null
        });

        await runnerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dispatcher.StopAcceptingRemoteInvocations();
        gate.SetResult();
        await dispatch;

        await runner.Received(1).RunAsync(Arg.Is<InvocationExecutionContext>(context => context.Package.InvocationId == package.InvocationId), Arg.Any<CancellationToken>());
        AssertEx.Equal(InvocationStatus.Completed, dispatcher.CurrentInvocation?.Status ?? InvocationStatus.Failed);
    }

    [Test]
    public async Task StopAcceptingRemoteInvocations_AbandonsAnAssignmentBlockedOnTheInvocationSlot()
    {
        // A second remote assignment BLOCKED waiting for the invocation slot (held by a running one) must be
        // released by the drain instead of hanging forever on the previously-uncancelable slot wait — and it must never
        // start. The already-running first assignment is unaffected.
        var runner = Substitute.For<IInvocationRunner>();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build();
        var second = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build();

        runner.RunAsync(Arg.Is<InvocationExecutionContext>(context => context.Package.InvocationId == first.InvocationId), Arg.Any<CancellationToken>())
              .Returns(_ =>
              {
                  firstEntered.TrySetResult();
                  return firstGate.Task;
              });

        var dispatcher = CreateDispatcher(runner);

        var firstDispatch = dispatcher.DispatchInvocationAssignedV2Async(new InvocationAssignedEnvelope
        {
            StorageMode = "PlainSync",
            Plain = first,
            Encrypted = null
        });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The second assignment passes the accept-guard, then blocks on the slot the running first assignment holds.
        var secondDispatch = dispatcher.DispatchInvocationAssignedV2Async(new InvocationAssignedEnvelope
        {
            StorageMode = "PlainSync",
            Plain = second,
            Encrypted = null
        });
        await AssertEx.StaysIncompleteAsync(secondDispatch, "The second assignment must be parked on the slot the first holds.");

        dispatcher.StopAcceptingRemoteInvocations();

        // The blocked second assignment is released (does not hang) and never runs; the first is still running.
        await secondDispatch.WaitAsync(TimeSpan.FromSeconds(5));
        await runner.DidNotReceive().RunAsync(Arg.Is<InvocationExecutionContext>(context => context.Package.InvocationId == second.InvocationId), Arg.Any<CancellationToken>());

        firstGate.SetResult();
        await firstDispatch;
        await runner.Received(1).RunAsync(Arg.Is<InvocationExecutionContext>(context => context.Package.InvocationId == first.InvocationId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReportInvocationThinkingChunkAsync_AccumulatesThinkingContentSeparately()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);
        var package = RuntimePackageBuilder.Valid().Build();
        await dispatcher.ReportInvocationAssignedAsync(package);

        await dispatcher.ReportInvocationThinkingChunkAsync(package.InvocationId, "Let me think...");
        await dispatcher.ReportInvocationStreamChunkAsync(package.InvocationId, "Hello");
        await dispatcher.ReportInvocationThinkingChunkAsync(package.InvocationId, " more thought");
        await dispatcher.ReportInvocationStreamChunkAsync(package.InvocationId, " world");

        var current = AssertEx.NotNull(dispatcher.CurrentInvocation);
        AssertEx.Equal("Let me think... more thought", current.StreamedThinkingContent);
        AssertEx.Equal(expected: 2, current.StreamedThinkingChunkCount);
        AssertEx.Equal("Hello world", current.StreamedContent);
        AssertEx.Equal(expected: 2, current.StreamedChunkCount);
    }

    [Test]
    public async Task ReportInvocationStreamChunkAsync_LongResponse_KeepsContentCorrectWithBoundedAllocations()
    {
        // Every streamed chunk clones the invocation snapshot. Content is now backed by an immutable
        // StreamingText copied by REFERENCE on clone, so a clone no longer materializes the whole accumulated response
        // per chunk (the old O(n^2) hot path). Assert (a) the final content is exactly the concatenation and (b)
        // streaming 20k chunks stays far below the allocation the old per-chunk ToString would have cost.
        var dispatcher = CreateDispatcher(Substitute.For<IInvocationRunner>());
        var package = RuntimePackageBuilder.Valid().Build();
        await dispatcher.ReportInvocationAssignedAsync(package);

        const int chunkCount = 20_000;
        const string chunk = "0123456789"; // 10 chars/chunk -> a ~200k-char final response

        // Warm up the JIT and the first-chunk allocations outside the measured window.
        await dispatcher.ReportInvocationStreamChunkAsync(package.InvocationId, chunk);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < chunkCount; i++)
        {
            await dispatcher.ReportInvocationStreamChunkAsync(package.InvocationId, chunk);
        }

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // Correctness read happens AFTER measuring so the one-time final materialization is not counted against the bound.
        var current = AssertEx.NotNull(dispatcher.CurrentInvocation);
        AssertEx.Equal((chunkCount + 1) * chunk.Length, current.StreamedContent.Length);
        AssertEx.Equal(chunkCount + 1, current.StreamedChunkCount);

        // The old clone materialized ~i*10 chars on chunk i -> ~sum(i)*10*2 bytes ~= 4 GB over 20k chunks. The immutable
        // accumulator makes per-chunk clone work O(1); assert well under a ceiling the O(n^2) path could never meet.
        AssertEx.True(allocatedBytes < 64L * 1024 * 1024,
            $"Streaming {chunkCount:N0} chunks allocated {allocatedBytes:N0} bytes; expected < 64 MiB (the old O(n^2) clone would allocate multiple GB).");
    }

    [Test]
    public async Task ReportInvocationCompletedAsync_PreservesGenerationDurationMsThroughSnapshotClone()
    {
        // Regression: Clone() copied the token fields but dropped GenerationDurationMs, so the cloned snapshot
        // delivered to the chat pump (both via the CurrentInvocation getter and the InvocationStateChanged event)
        // persisted a null duration even though the runner reported one. Assert both clone paths carry it.
        var dispatcher = CreateDispatcher(Substitute.For<IInvocationRunner>());
        var package = RuntimePackageBuilder.Valid().Build();
        await dispatcher.ReportInvocationAssignedAsync(package);

        InvocationState? lastEventState = null;
        dispatcher.InvocationStateChanged += (_, args) => lastEventState = args.State;

        await dispatcher.ReportInvocationCompletedAsync(package.InvocationId, inputTokens: 10, outputTokens: 3, totalTokens: 13, reasoningTokens: 1,
            generationDurationMs: 1234, finishReason: "length");

        // The getter returns Clone(CurrentInvocation): the duration must survive that copy.
        var current = AssertEx.NotNull(dispatcher.CurrentInvocation);
        AssertEx.Equal(expected: 1234L, current.GenerationDurationMs);
        AssertEx.Equal(expected: 3, current.OutputTokens);

        // Same failure mode, same clone: a benchmark reads the finish reason off the TERMINAL snapshot, so a field the
        // clone drops travels as null and a truncated run looks complete.
        AssertEx.Equal("length", current.FinishReason);

        // The event payload is also a Clone of the state; the pump consumes this snapshot.
        var eventState = AssertEx.NotNull(lastEventState);
        AssertEx.Equal(expected: 1234L, eventState.GenerationDurationMs);
        AssertEx.Equal("length", eventState.FinishReason);
    }

    [Test]
    public async Task ReportInvocationChunkAsync_WhenChunkIsWhitespaceOnly_PreservesWhitespace()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);
        var package = RuntimePackageBuilder.Valid().Build();
        await dispatcher.ReportInvocationAssignedAsync(package);

        await dispatcher.ReportInvocationStreamChunkAsync(package.InvocationId, "Hello");
        await dispatcher.ReportInvocationStreamChunkAsync(package.InvocationId, " ");
        await dispatcher.ReportInvocationStreamChunkAsync(package.InvocationId, "world");
        await dispatcher.ReportInvocationThinkingChunkAsync(package.InvocationId, "Think");
        await dispatcher.ReportInvocationThinkingChunkAsync(package.InvocationId, "\n");
        await dispatcher.ReportInvocationThinkingChunkAsync(package.InvocationId, "again");

        var current = AssertEx.NotNull(dispatcher.CurrentInvocation);
        AssertEx.Equal("Hello world", current.StreamedContent);
        AssertEx.Equal(expected: 3, current.StreamedChunkCount);
        AssertEx.Equal("Think\nagain", current.StreamedThinkingContent);
        AssertEx.Equal(expected: 3, current.StreamedThinkingChunkCount);
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_SetsCurrentInvocation()
    {
        var dispatcher = CreateDispatcher(Substitute.For<IInvocationRunner>());
        var package = RuntimePackageBuilder.Valid().Build();

        await dispatcher.DispatchInvocationAssignedAsync(CreateEncryptedPackage(package));

        var current = AssertEx.NotNull(dispatcher.CurrentInvocation);
        AssertEx.Equal(package.InvocationId, current.InvocationId);
        AssertEx.Equal(package.ConversationId, current.ConversationId);
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_RaisesInvocationStateChangedEvent()
    {
        var dispatcher = CreateDispatcher(Substitute.For<IInvocationRunner>());
        var package = RuntimePackageBuilder.Valid().Build();
        var eventCount = 0;
        dispatcher.InvocationStateChanged += (_, _) => eventCount++;

        await dispatcher.DispatchInvocationAssignedAsync(CreateEncryptedPackage(package));

        AssertEx.True(eventCount >= 2);
    }

    [Test]
    public async Task DispatchToolCallResultAsync_CallsResolveToolCallResult()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);
        var evt = new ToolCallResultEvent
        {
            RequestId = "req-1",
            Result = "ok"
        };

        await dispatcher.DispatchToolCallResultAsync(evt);

        runner.Received(1).ResolveToolCallResult(evt);
    }

    [Test]
    public async Task DispatchApprovalResolvedAsync_CallsResolveApprovalResult()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);
        var evt = new ApprovalResolvedEvent("req-1", Approved: true);

        await dispatcher.DispatchApprovalResolvedAsync(evt);

        runner.Received(1).ResolveApprovalResult(evt);
        runner.DidNotReceive().Cancel(Arg.Any<Guid>());
        runner.DidNotReceive().CancelAll();
        runner.DidNotReceive().ResolveToolCallResult(Arg.Any<ToolCallResultEvent>());
        await runner.DidNotReceive().RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DispatchInvocationCancelledAsync_CallsCancelOnRunner()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);
        var package = RuntimePackageBuilder.Valid().Build();
        await dispatcher.DispatchInvocationAssignedAsync(CreateEncryptedPackage(package));

        await dispatcher.DispatchInvocationCancelledAsync(new InvocationCancelledEvent(package.InvocationId, "cancelled"));

        runner.Received(1).Cancel(package.InvocationId);
    }

    [Test]
    public async Task DispatchDisconnectRequestedAsync_CallsCancelAllOnRunner()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);

        await dispatcher.DispatchDisconnectRequestedAsync(new DisconnectRequestedEvent
        {
            Reason = "shutdown"
        });

        runner.Received(1).CancelAll();
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_WhenRunnerThrows_MarksInvocationFailed()
    {
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromException(new InvalidOperationException("boom")));

        var dispatcher = CreateDispatcher(runner);
        var package = RuntimePackageBuilder.Valid().Build();

        await dispatcher.DispatchInvocationAssignedAsync(CreateEncryptedPackage(package));

        var current = AssertEx.NotNull(dispatcher.CurrentInvocation);
        AssertEx.Equal(package.InvocationId, current.InvocationId);
        AssertEx.Equal(InvocationStatus.Failed, current.Status);
        AssertEx.Equal("boom", current.Error);
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_WhenAadMismatch_EmitsInvocationKeyMismatch()
    {
        var runner = Substitute.For<IInvocationRunner>();
#pragma warning disable CA2000 // The dispatcher retains this test registry; it has no disposal contract, and its isolated NSec key lives only for this test process.
        var nodeKeyRegistry = new FakeNodeKeyRegistry();
#pragma warning restore CA2000
        var sender = new MockHubMessageSender();
        var encryptedPackage = CreateEncryptedPackage(RuntimePackageBuilder.Valid().Build());
        var assembler = new FakeRuntimePackageEnvelopeAssembler(_ => throw new InvalidOperationException("Encrypted runtime package AAD did not match the expected envelope metadata."));

        var dispatcher = new WorkerEventDispatcher(runner,
            assembler,
            new Lazy<IHubMessageSender>(() => sender),
            nodeKeyRegistry,
            Substitute.For<IInvocationHistory>(),
            CreateRemotePersistenceCoordinator(),
            NullLogger<WorkerEventDispatcher>.Instance);

        await dispatcher.DispatchInvocationAssignedAsync(encryptedPackage);

        AssertEx.ContainsSingle(sender.SentKeyMismatches,
            mismatch => mismatch.MessageId == encryptedPackage.MessageId
                        && mismatch.Reason == "aad-mismatch"
                        && mismatch.NodeKeyIdUsed == "active-key");
        await runner.DidNotReceive().RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_WhenRetiredKeyExpired_EmitsInvocationKeyMismatch()
    {
        var runner = Substitute.For<IInvocationRunner>();
#pragma warning disable CA2000 // The dispatcher retains this test registry; it has no disposal contract, and its isolated NSec key lives only for this test process.
        var nodeKeyRegistry = new FakeNodeKeyRegistry(new NodeKeyResolution
        {
            RequestedKeyId = "retired-key-1",
            Status = NodeKeyLookupStatus.RetiredExpired,
            KeyIdUsed = "retired-key-1"
        });
#pragma warning restore CA2000
        var sender = new MockHubMessageSender();
        var encryptedPackage = CreateEncryptedPackage(RuntimePackageBuilder.Valid().Build());

        var dispatcher = new WorkerEventDispatcher(runner,
            new FakeRuntimePackageEnvelopeAssembler(_ => throw new InvalidOperationException("assemble should not run for expired retired keys")),
            new Lazy<IHubMessageSender>(() => sender),
            nodeKeyRegistry,
            Substitute.For<IInvocationHistory>(),
            CreateRemotePersistenceCoordinator(),
            NullLogger<WorkerEventDispatcher>.Instance);

        await dispatcher.DispatchInvocationAssignedAsync(encryptedPackage);

        AssertEx.ContainsSingle(sender.SentKeyMismatches,
            mismatch => mismatch.MessageId == encryptedPackage.MessageId
                        && mismatch.Reason == "retired-key"
                        && mismatch.NodeKeyIdUsed == "retired-key-1");
        await runner.DidNotReceive().RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>());
    }

    private static WorkerEventDispatcher CreateDispatcher(IInvocationRunner runner)
    {
#pragma warning disable CA2000 // The returned dispatcher retains this registry; it has no disposal contract, and the isolated NSec key is process-scoped test data.
        var nodeKeyRegistry = new FakeNodeKeyRegistry();
#pragma warning restore CA2000
        var sender = new MockHubMessageSender();
        var assembler = new FakeRuntimePackageEnvelopeAssembler(encryptedPackage =>
        {
            var runtimePackage = DeserializeRuntimePackage(encryptedPackage);
            return InvocationExecutionContext.Create(runtimePackage,
                encryptedPackage.MessageId,
                encryptedPackage.EpochVersion,
                new byte[32]);
        });

        return CreateDispatcher(runner, assembler, sender, nodeKeyRegistry);
    }

    private static WorkerEventDispatcher CreateDispatcher(IInvocationRunner runner,
        IRuntimePackageEnvelopeAssembler assembler,
        IHubMessageSender hubMessageSender,
        INodeKeyRegistry nodeKeyRegistry)
    {
        return new WorkerEventDispatcher(runner,
            assembler,
            new Lazy<IHubMessageSender>(() => hubMessageSender),
            nodeKeyRegistry,
            Substitute.For<IInvocationHistory>(),
            CreateRemotePersistenceCoordinator(),
            NullLogger<WorkerEventDispatcher>.Instance);
    }

    private static INodeChatRemotePersistenceCoordinator CreateRemotePersistenceCoordinator()
    {
        // A real session over a substitute pump that returns benign results, so the dispatcher's persistence
        // drain runs without NPEs while these tests focus on the agent-run wiring.
        var pump = Substitute.For<INodeChatInvocationPump>();
        pump.FlushDeltaAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<InvocationState>(), Arg.Any<NodeChatPumpCursor>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new NodeChatPumpFlushResult(callInfo.ArgAt<NodeChatPumpCursor>(2), Persisted: null, ContentDelta: null, ReasoningDelta: null));

        var coordinator = Substitute.For<INodeChatRemotePersistenceCoordinator>();
        coordinator.BeginAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo =>
                   {
                       var package = callInfo.ArgAt<RuntimePackage>(0);
                       return new NodeChatRemotePersistenceSession(pump,
                           new NodeChatMessageCorrelation(package.ConversationId, Guid.NewGuid(), package.InvocationId),
                           package.ModelProfile);
                   });

        return coordinator;
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_WhenConfigHashMismatch_SendsEncryptedFailure()
    {
        var runner = Substitute.For<IInvocationRunner>();
#pragma warning disable CA2000 // The dispatcher retains this test registry; it has no disposal contract, and its isolated NSec key lives only for this test process.
        var nodeKeyRegistry = new FakeNodeKeyRegistry();
#pragma warning restore CA2000
        var sender = new MockHubMessageSender();
        var encryptedPackage = CreateEncryptedPackage(RuntimePackageBuilder.Valid().Build());
        var dispatcher = new WorkerEventDispatcher(runner,
            new FakeRuntimePackageEnvelopeAssembler(_ => throw new InvalidOperationException("runtime-package-config-hash-mismatch")),
            new Lazy<IHubMessageSender>(() => sender),
            nodeKeyRegistry,
            Substitute.For<IInvocationHistory>(),
            CreateRemotePersistenceCoordinator(),
            NullLogger<WorkerEventDispatcher>.Instance);

        await dispatcher.DispatchInvocationAssignedAsync(encryptedPackage);

        AssertEx.ContainsSingle(sender.SentEncryptedFailures,
            failure => failure.ConversationId == encryptedPackage.ConversationId
                       && failure.MessageId == encryptedPackage.MessageId
                       && failure.FailureCategory == nameof(FailureCategory.HashMismatch)
                       && failure.Error == "runtime-package-config-hash-mismatch");
        await runner.DidNotReceive().RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_WhenHistoryHashMismatch_SendsEncryptedFailure()
    {
        var runner = Substitute.For<IInvocationRunner>();
#pragma warning disable CA2000 // The dispatcher retains this test registry; it has no disposal contract, and its isolated NSec key lives only for this test process.
        var nodeKeyRegistry = new FakeNodeKeyRegistry();
#pragma warning restore CA2000
        var sender = new MockHubMessageSender();
        var encryptedPackage = CreateEncryptedPackage(RuntimePackageBuilder.Valid().Build());
        var dispatcher = new WorkerEventDispatcher(runner,
            new FakeRuntimePackageEnvelopeAssembler(_ => throw new InvalidOperationException("runtime-package-history-hash-mismatch")),
            new Lazy<IHubMessageSender>(() => sender),
            nodeKeyRegistry,
            Substitute.For<IInvocationHistory>(),
            CreateRemotePersistenceCoordinator(),
            NullLogger<WorkerEventDispatcher>.Instance);

        await dispatcher.DispatchInvocationAssignedAsync(encryptedPackage);

        AssertEx.ContainsSingle(sender.SentEncryptedFailures,
            failure => failure.ConversationId == encryptedPackage.ConversationId
                       && failure.MessageId == encryptedPackage.MessageId
                       && failure.FailureCategory == nameof(FailureCategory.HashMismatch)
                       && failure.Error == "runtime-package-history-hash-mismatch");
        await runner.DidNotReceive().RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_WhenEnvelopeIsValid_BuildsRealRuntimePackageFromMixedEnvelope()
    {
        var runner = Substitute.For<IInvocationRunner>();
        InvocationExecutionContext? capturedContext = null;
        byte[]? capturedEpochKey = null;
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(callInfo =>
              {
                  capturedContext = callInfo.Arg<InvocationExecutionContext>();
                  capturedEpochKey = capturedContext.EpochKey.ToArray();
                  return Task.CompletedTask;
              });

#pragma warning disable CA2000 // The dispatcher and real assembler share this registry for the test; neither owns the other's dependency, so process teardown releases the isolated key.
        var nodeKeyRegistry = new FakeNodeKeyRegistry();
#pragma warning restore CA2000
        var sender = new MockHubMessageSender();
        var historyEntryOne = CreateHistoryEntry(MessageRole.System, sortOrder: 10);
        var historyEntryTwo = CreateHistoryEntry(MessageRole.Assistant, sortOrder: 20);
        var encryptedPackage = CreateMixedEnvelopePackage([historyEntryOne, historyEntryTwo]);
        var expectedEpochKey = Enumerable.Range(start: 1, count: 32).Select(static value => (byte)value).ToArray();
        var envelopeCryptoService = Substitute.For<IEnvelopeCryptoService>();
        envelopeCryptoService.DecryptConversationMessage(encryptedPackage.ConversationId, historyEntryOne, Arg.Any<Key>())
                             .Returns(_ => new EnvelopeDecryptionResult("system guidance"u8.ToArray(), new byte[32]));
        envelopeCryptoService.DecryptConversationMessage(encryptedPackage.ConversationId, historyEntryTwo, Arg.Any<Key>())
                             .Returns(_ => new EnvelopeDecryptionResult("assistant reply"u8.ToArray(), new byte[32]));
        envelopeCryptoService.DecryptRuntimePackage(encryptedPackage, Arg.Any<Key>())
                             .Returns(_ => new EnvelopeDecryptionResult("latest user message"u8.ToArray(), expectedEpochKey.ToArray()));

        var validator = Substitute.For<IRuntimePackageValidator>();
        validator.Validate(Arg.Any<RuntimePackage>()).Returns(RuntimePackageValidationResult.Success);

        var assembler = new RuntimePackageEnvelopeAssembler(envelopeCryptoService, nodeKeyRegistry, validator);
        var dispatcher = CreateDispatcher(runner, assembler, sender, nodeKeyRegistry);

        await dispatcher.DispatchInvocationAssignedAsync(encryptedPackage);

        await runner.Received(1).RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>());

        var context = AssertEx.NotNull(capturedContext);
        AssertEx.Equal(encryptedPackage.InvocationId, context.Package.InvocationId);
        AssertEx.Equal(encryptedPackage.ConversationId, context.Package.ConversationId);
        AssertEx.Equal(encryptedPackage.ClientNodeId, context.Package.ClientNodeId);
        AssertEx.Equal(encryptedPackage.AgentDefinitionVersion, context.Package.AgentDefinitionVersion);
        AssertEx.Equal(encryptedPackage.ResolvedSystemPrompt, context.Package.ResolvedSystemPrompt);
        AssertEx.True(string.Equals(encryptedPackage.ModelProfile, context.Package.ModelProfile, StringComparison.Ordinal));
        AssertEx.Equal(encryptedPackage.ConfigHash, context.Package.ConfigHash);
        AssertEx.Equal(encryptedPackage.Timeouts.InvocationTimeoutSeconds, context.Package.Timeouts.InvocationTimeoutSeconds);
        AssertEx.Equal(encryptedPackage.Timeouts.ToolCallTimeoutSeconds, context.Package.Timeouts.ToolCallTimeoutSeconds);
        AssertEx.Equal(encryptedPackage.Timeouts.StreamIdleTimeoutSeconds, context.Package.Timeouts.StreamIdleTimeoutSeconds);
        AssertEx.Equal(expected: 1, context.Package.AllowedTools.Count);
        AssertEx.Equal("open_url", context.Package.AllowedTools[0].Name);
        AssertEx.Equal(ToolLocation.ApiSide, context.Package.AllowedTools[0].Location);
        AssertEx.Equal("{\"type\":\"object\"}", context.Package.AllowedTools[0].ParameterSchema);
        AssertEx.Equal(expected: 3, context.Package.ConversationContext.Count);
        AssertEx.Equal(historyEntryOne.Id, context.Package.ConversationContext[0].Id);
        AssertEx.Equal(MessageRole.System, context.Package.ConversationContext[0].Role);
        AssertEx.Equal("system guidance", context.Package.ConversationContext[0].Content);
        AssertEx.Equal(expected: 10, context.Package.ConversationContext[0].SortOrder);
        AssertEx.Equal(historyEntryTwo.Id, context.Package.ConversationContext[1].Id);
        AssertEx.Equal(MessageRole.Assistant, context.Package.ConversationContext[1].Role);
        AssertEx.Equal("assistant reply", context.Package.ConversationContext[1].Content);
        AssertEx.Equal(expected: 20, context.Package.ConversationContext[1].SortOrder);
        AssertEx.Equal(encryptedPackage.MessageId, context.Package.ConversationContext[2].Id);
        AssertEx.Equal(MessageRole.User, context.Package.ConversationContext[2].Role);
        AssertEx.Equal("latest user message", context.Package.ConversationContext[2].Content);
        AssertEx.Equal(expected: 21, context.Package.ConversationContext[2].SortOrder);
        AssertEx.Equal(encryptedPackage.MessageId, context.MessageId);
        AssertEx.Equal(encryptedPackage.EpochVersion, context.EpochVersion);
        AssertEx.True((capturedEpochKey ?? []).SequenceEqual(expectedEpochKey));

        validator.Received(1).Validate(Arg.Is<RuntimePackage>(package =>
            package.ConversationContext.Count == 3
            && package.ConversationContext[2].Content == "latest user message"
            && package.ModelProfile == encryptedPackage.ModelProfile));
    }

    private static EncryptedRuntimePackageDto CreateEncryptedPackage(RuntimePackage runtimePackage)
    {
        return new EncryptedRuntimePackageDto
        {
            InvocationId = runtimePackage.InvocationId,
            ConversationId = runtimePackage.ConversationId,
            ClientNodeId = runtimePackage.ClientNodeId,
            MessageId = Guid.NewGuid(),
            EpochVersion = 1,
            AgentDefinitionVersion = runtimePackage.AgentDefinitionVersion,
            ResolvedSystemPrompt = runtimePackage.ResolvedSystemPrompt,
            AllowedTools = [],
            Timeouts = runtimePackage.Timeouts,
            ConfigHash = runtimePackage.ConfigHash,
            ConversationContext = [],
            ConversationContextHash = "4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945",
            NodeWrappedEpochKey = new byte[]
            {
                1,
                2,
                3
            },
            ClientEphemeralPublicKey = new byte[]
            {
                4,
                5,
                6
            },
            Ciphertext = JsonSerializer.SerializeToUtf8Bytes(runtimePackage, SerializerOptions),
            ContentIv = new byte[]
            {
                7,
                8,
                9
            },
            Aad = "message|aad-placeholder"
        };
    }

    private static EncryptedRuntimePackageDto CreateMixedEnvelopePackage(IReadOnlyList<EncryptedConversationMessageDto>? historyEntries = null)
    {
        var conversationContext = historyEntries?.ToList() ?? [];
        var package = new EncryptedRuntimePackageDto
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            ClientNodeId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            EpochVersion = 7,
            AgentDefinitionVersion = 7,
            ResolvedSystemPrompt = "You are a helpful local AI assistant.",
            AllowedTools =
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "open_url",
                    Description = "Open a URL in the worker browser",
                    Schema = "{\"type\":\"object\"}"
                }
            ],
            ModelProfile = "balanced-local-v1",
            Timeouts = new TimeoutSettings
            {
                InvocationTimeoutSeconds = 300,
                ToolCallTimeoutSeconds = 60,
                StreamIdleTimeoutSeconds = 30
            },
            ConfigHash = string.Empty,
            ConversationContext = conversationContext,
            ConversationContextHash = string.Empty,
            NodeWrappedEpochKey = new byte[]
            {
                1,
                2,
                3
            },
            ClientEphemeralPublicKey = new byte[]
            {
                4,
                5,
                6
            },
            Ciphertext = new byte[]
            {
                7,
                8,
                9
            },
            ContentIv = new byte[]
            {
                10,
                11,
                12
            },
            Aad = "message|aad-placeholder"
        };

        return package with
        {
            ConfigHash = RuntimePackageConfigHash.Compute(package),
            ConversationContextHash = RuntimePackageHistoryHash.Compute(package.ConversationContext)
        };
    }

    private static EncryptedConversationMessageDto CreateHistoryEntry(MessageRole role, int sortOrder)
    {
        return new EncryptedConversationMessageDto
        {
            Id = Guid.NewGuid(),
            Role = role,
            SortOrder = sortOrder,
            EpochVersion = 7,
            Aad = $"message|history-{sortOrder}",
            NodeWrappedEpochKey = new byte[]
            {
                1,
                2,
                3
            },
            ClientEphemeralPublicKey = new byte[]
            {
                4,
                5,
                6
            },
            Ciphertext = new byte[]
            {
                7,
                8,
                9
            },
            ContentIv = new byte[]
            {
                10,
                11,
                12
            }
        };
    }

    private static RuntimePackage DeserializeRuntimePackage(EncryptedRuntimePackageDto encryptedPackage)
    {
        var invocationId = encryptedPackage.InvocationId;
        var conversationId = encryptedPackage.ConversationId;
        var clientNodeId = encryptedPackage.ClientNodeId;

        return JsonSerializer.Deserialize<RuntimePackage>(encryptedPackage.Ciphertext.Span, SerializerOptions)
               ?? RuntimePackageBuilder.Valid()
                                       .WithInvocationId(invocationId)
                                       .WithConversationId(conversationId)
                                       .WithClientNodeId(clientNodeId)
                                       .Build();
    }

    private sealed class FakeNodeKeyRegistry : INodeKeyRegistry
    {
        private readonly Key _privateKey = Key.Create(KeyAgreementAlgorithm.X25519);
        private readonly NodeKeyResolution? _resolution;

        public FakeNodeKeyRegistry()
        {
        }

        public FakeNodeKeyRegistry(NodeKeyResolution resolution)
        {
            _resolution = resolution;
        }

        public string ActiveKeyId => "active-key";

        public PublicKey ActivePublicKey => _privateKey.PublicKey;

        public IReadOnlyList<NodeKeyResolution> ResolveGraceEligible()
        {
            return
            [
                _resolution ?? new NodeKeyResolution
                {
                    RequestedKeyId = ActiveKeyId,
                    Status = NodeKeyLookupStatus.Active,
                    KeyIdUsed = ActiveKeyId,
                    PrivateKey = _privateKey,
                    PublicKey = _privateKey.PublicKey
                }
            ];
        }

        public NodeKeyResolution Resolve(string nodeKeyId)
        {
            if (_resolution is not null)
            {
                return _resolution;
            }

            return new NodeKeyResolution
            {
                RequestedKeyId = nodeKeyId,
                Status = NodeKeyLookupStatus.Active,
                KeyIdUsed = ActiveKeyId,
                PrivateKey = _privateKey,
                PublicKey = _privateKey.PublicKey
            };
        }

        public void Rotate(string nodeKeyId, Key privateKey)
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
            _privateKey.Dispose();
        }
    }

    private sealed class FakeRuntimePackageEnvelopeAssembler : IRuntimePackageEnvelopeAssembler
    {
        private readonly Func<EncryptedRuntimePackageDto, InvocationExecutionContext> _assemble;

        public FakeRuntimePackageEnvelopeAssembler(Func<EncryptedRuntimePackageDto, InvocationExecutionContext> assemble)
        {
            _assemble = assemble;
        }

        public InvocationExecutionContext Assemble(EncryptedRuntimePackageDto package)
        {
            return _assemble(package);
        }
    }
}
