namespace XE_Local_AI_Engine.Tests.Shutdown;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.DeadLetter.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Shutdown;
using XE_Local_AI_Engine.Client.Services.Shutdown.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class WorkerShutdownDrainServiceTests
{
    [Test]
    public async Task ApplicationStopping_InvokesShutdownDrainSequenceInOrder()
    {
        var components = RecordingShutdownComponents.Create(true);
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services => ReplaceShutdownComponents(services, components)
        };

        _ = factory.CreateClient();

        await factory.DisposeAsync();

        AssertEx.Equal("stop-accepting|await-active-invocations|flush-dead-letter|remove-dead-letter|disconnect-worker-hub",
            components.Operations.ToDelimitedString());
    }

    [Test]
    public async Task DrainAsync_WhenActiveInvocationCompletesDuringDrain_DisconnectsAfterDrainCompletes()
    {
        var components = RecordingShutdownComponents.Create(false);
        components.InvocationRunner.ActiveInvocationCountValue = 1;
        components.InvocationRunner.UseCompletionGate();

        var service = components.CreateService();
        var drainTask = service.DrainAsync();

        await components.InvocationRunner.DrainStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        AssertEx.Equal(expected: 0, components.WorkerHubConnection.DisconnectAsyncCallCount);

        components.InvocationRunner.CompleteDrain(true);
        var result = await drainTask.WaitAsync(TimeSpan.FromSeconds(2));

        AssertEx.True(result.Succeeded);
        AssertEx.Equal(expected: 1, components.WorkerHubConnection.DisconnectAsyncCallCount);
        AssertEx.Equal("stop-accepting|await-active-invocations|active-invocations-drained|disconnect-worker-hub",
            components.Operations.ToDelimitedString());
    }

    [Test]
    public async Task DrainAsync_WhenCleanStopHasNoDeadLetters_DoesNotCreateDeadLetterEntries()
    {
        var components = RecordingShutdownComponents.Create(false);
        var service = components.CreateService();

        var result = await service.DrainAsync();

        AssertEx.True(result.Succeeded);
        AssertEx.Empty(components.DeadLetterStore.Enqueued);
        AssertEx.Empty(components.DeadLetterStore.Pending);
        AssertEx.Equal(expected: 1, components.WorkerHubConnection.DisconnectAsyncCallCount);
    }

    [Test]
    public async Task DrainAsync_WhenExistingDeadLetterCannotFlush_PreservesExistingEntry()
    {
        var components = RecordingShutdownComponents.Create(true);
        components.HubMessageSender.ThrowOnFailedSend = true;
        var pendingInvocationId = components.DeadLetterStore.Pending.Single().InvocationId;
        var service = components.CreateService();

        var result = await service.DrainAsync();

        AssertEx.True(result.Succeeded);
        AssertEx.Empty(components.DeadLetterStore.Enqueued);
        AssertEx.Empty(components.DeadLetterStore.Removed);
        AssertEx.ContainsSingle(components.DeadLetterStore.Pending, entry => entry.InvocationId == pendingInvocationId);
    }

    [Test]
    public async Task DrainAsync_DisconnectsWorkerHubExactlyOnceOnCleanPath()
    {
        var components = RecordingShutdownComponents.Create(false);
        var service = components.CreateService();

        var result = await service.DrainAsync();

        AssertEx.True(result.Succeeded);
        AssertEx.Equal(expected: 1, components.WorkerHubConnection.DisconnectAsyncCallCount);
    }

    [Test]
    public async Task DrainAsync_WhenFlushAndDisconnectNeverComplete_CompletesWithinDeadlineAndLogsDrops()
    {
        var components = RecordingShutdownComponents.Create(hasPendingDeadLetter: true);
        components.HubMessageSender.BlockUntilCancelled = true; // dead-letter flush never completes on its own
        components.WorkerHubConnection.BlockUntilCancelled = true; // hub disconnect never completes on its own

        var service = components.CreateService(TimeSpan.FromMilliseconds(200));

        // If the whole-drain deadline is honored, DrainAsync returns near the 200ms deadline. If it were unbounded (the
        // pre-fix behavior), the blocking fakes would hang and this WaitAsync would throw — the test would fail.
        var result = await service.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));

        AssertEx.False(result.Succeeded);
        AssertEx.Contains(result.Diagnostics, entry => entry == "flush-dead-letter-outbox:deadline-exceeded");
        AssertEx.Contains(result.Diagnostics, entry => entry == "disconnect-worker-hub:deadline-exceeded");
        // The unflushed dead-letter entry was never removed — it stays queued for a later run.
        AssertEx.NotEmpty(components.DeadLetterStore.Pending);
        AssertEx.Empty(components.DeadLetterStore.Removed);
    }

    private static void ReplaceShutdownComponents(IServiceCollection services, RecordingShutdownComponents components)
    {
        services.RemoveAll<IWorkerEventDispatcher>();
        services.RemoveAll<IInvocationRunner>();
        services.RemoveAll<IDeadLetterStore>();
        services.RemoveAll<IHubMessageSender>();
        services.RemoveAll<IWorkerHubConnection>();

        services.AddSingleton<IWorkerEventDispatcher>(components.Dispatcher);
        services.AddSingleton<IInvocationRunner>(components.InvocationRunner);
        services.AddSingleton<IDeadLetterStore>(components.DeadLetterStore);
        services.AddSingleton<IHubMessageSender>(components.HubMessageSender);
        services.AddSingleton<IWorkerHubConnection>(components.WorkerHubConnection);
    }

    private sealed class RecordingShutdownComponents
    {
        private RecordingShutdownComponents(OperationLog operations,
            RecordingWorkerEventDispatcher dispatcher,
            RecordingInvocationRunner invocationRunner,
            RecordingDeadLetterStore deadLetterStore,
            RecordingHubMessageSender hubMessageSender,
            RecordingWorkerHubConnection workerHubConnection)
        {
            Operations = operations;
            Dispatcher = dispatcher;
            InvocationRunner = invocationRunner;
            DeadLetterStore = deadLetterStore;
            HubMessageSender = hubMessageSender;
            WorkerHubConnection = workerHubConnection;
        }

        public OperationLog Operations { get; }

        public RecordingWorkerEventDispatcher Dispatcher { get; }

        public RecordingInvocationRunner InvocationRunner { get; }

        public RecordingDeadLetterStore DeadLetterStore { get; }

        public RecordingHubMessageSender HubMessageSender { get; }

        public RecordingWorkerHubConnection WorkerHubConnection { get; }

        public static RecordingShutdownComponents Create(bool hasPendingDeadLetter)
        {
            var operations = new OperationLog();
            var store = new RecordingDeadLetterStore(operations);
            if (hasPendingDeadLetter)
            {
                store.Pending.Add(CreatePayload());
            }

#pragma warning disable CA2000 // The test component container owns this fake through the lifetime of each test.
            return new RecordingShutdownComponents(operations,
                new RecordingWorkerEventDispatcher(operations),
                new RecordingInvocationRunner(operations),
                store,
                new RecordingHubMessageSender(operations),
                new RecordingWorkerHubConnection(operations));
#pragma warning restore CA2000
        }

        public WorkerShutdownDrainService CreateService(TimeSpan? drainTimeout = null)
        {
            var deadLetterFlushService = new DeadLetterFlushService(DeadLetterStore,
                new Lazy<IHubMessageSender>(() => HubMessageSender),
                NullLogger<DeadLetterFlushService>.Instance);

            var options = drainTimeout is { } timeout
                ? new WorkerShutdownDrainOptions
                {
                    DrainTimeout = timeout
                }
                : new WorkerShutdownDrainOptions();

            return new WorkerShutdownDrainService(Dispatcher,
                InvocationRunner,
                deadLetterFlushService,
                WorkerHubConnection,
                Options.Create(options),
                NullLogger<WorkerShutdownDrainService>.Instance);
        }

        private static InvocationFailedPayload CreatePayload()
        {
            return new InvocationFailedPayload
            {
                InvocationId = Guid.NewGuid(),
                Error = "pending failure"
            };
        }
    }

    private sealed class OperationLog
    {
        private readonly List<string> _items = [];
        private readonly object _sync = new();

        public void Add(string item)
        {
            lock (_sync)
            {
                _items.Add(item);
            }
        }

        public string ToDelimitedString()
        {
            lock (_sync)
            {
                return string.Join(separator: '|', _items);
            }
        }
    }

    private sealed class RecordingWorkerEventDispatcher(OperationLog operations) : IWorkerEventDispatcher
    {
        public InvocationState? CurrentInvocation => null;

        public bool IsAcceptingRemoteInvocations { get; private set; } = true;

        event EventHandler<InvocationStateChangedEventArgs>? IWorkerEventDispatcher.InvocationStateChanged
        {
            add => _ = value;
            remove => _ = value;
        }

        event EventHandler<ToolCallLifecycleChangedEventArgs>? IWorkerEventDispatcher.ToolCallLifecycleChanged
        {
            add => _ = value;
            remove => _ = value;
        }

        event EventHandler<TurnNoticeChangedEventArgs>? IWorkerEventDispatcher.TurnNoticeChanged
        {
            add => _ = value;
            remove => _ = value;
        }

        event EventHandler<ApprovalRequestedChangedEventArgs>? IWorkerEventDispatcher.ApprovalRequestedChanged
        {
            add => _ = value;
            remove => _ = value;
        }

        event EventHandler<UserQuestionRequestedChangedEventArgs>? IWorkerEventDispatcher.UserQuestionRequestedChanged
        {
            add => _ = value;
            remove => _ = value;
        }

        public void StopAcceptingRemoteInvocations()
        {
            IsAcceptingRemoteInvocations = false;
            operations.Add("stop-accepting");
        }

        public Task DispatchInvocationAssignedAsync(EncryptedRuntimePackageDto package)
        {
            return Task.CompletedTask;
        }

        public Task DispatchInvocationAssignedV2Async(InvocationAssignedEnvelope envelope)
        {
            return Task.CompletedTask;
        }

        public Task DispatchToolCallResultAsync(ToolCallResultEvent evt)
        {
            return Task.CompletedTask;
        }

        public Task DispatchDisconnectRequestedAsync(DisconnectRequestedEvent evt)
        {
            return Task.CompletedTask;
        }

        public Task DispatchApprovalResolvedAsync(ApprovalResolvedEvent evt, ApprovalScope scope = ApprovalScope.Once)
        {
            return Task.CompletedTask;
        }

        public Task DispatchInvocationCancelledAsync(InvocationCancelledEvent evt)
        {
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> ReportInvocationAssignedAsync(RuntimePackage package, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IAsyncDisposable>(NoopLease.Instance);
        }

        public Task ReportInvocationStreamChunkAsync(Guid invocationId, string chunk)
        {
            return Task.CompletedTask;
        }

        public Task ReportInvocationThinkingChunkAsync(Guid invocationId, string chunk)
        {
            return Task.CompletedTask;
        }

        public Task ReportInvocationPhaseAsync(Guid invocationId, InvocationRuntimePhase phase)
        {
            return Task.CompletedTask;
        }

        public Task ReportInvocationCompletedAsync(Guid invocationId, int? inputTokens = null, int? outputTokens = null, int? totalTokens = null, int? reasoningTokens = null,
            long? generationDurationMs = null, string? finishReason = null, InvocationThroughput? throughput = null)
        {
            return Task.CompletedTask;
        }

        public Task ReportToolSchemaTokensAsync(Guid invocationId, long? toolSchemaTokens, int? maxToolSchemaTokens)
        {
            return Task.CompletedTask;
        }

        public Task ReportInvocationFailedAsync(Guid invocationId, string failureMessage, FailureCategory failureCategory)
        {
            return Task.CompletedTask;
        }

        public Task ReportToolCallRequestedAsync(ToolCallRequestPayload payload)
        {
            return Task.CompletedTask;
        }

        public Task ReportApprovalRequestedAsync(ApprovalRequestPayload payload)
        {
            return Task.CompletedTask;
        }

        public Task ReportToolCallLifecycleAsync(ToolCallLifecyclePayload payload)
        {
            return Task.CompletedTask;
        }

        public Task ReportTurnNoticeAsync(TurnNoticePayload payload)
        {
            return Task.CompletedTask;
        }

        public Task ReportApprovalLifecycleAsync(ApprovalLifecyclePayload payload)
        {
            return Task.CompletedTask;
        }

        public Task ReportUserQuestionAsync(UserQuestionLifecyclePayload payload)
        {
            return Task.CompletedTask;
        }

        public Task DispatchUserQuestionAnsweredAsync(UserQuestionAnsweredEvent evt)
        {
            return Task.CompletedTask;
        }

        private sealed class NoopLease : IAsyncDisposable
        {
            public static readonly NoopLease Instance = new();

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingInvocationRunner(OperationLog operations) : IInvocationRunner
    {
        private TaskCompletionSource<bool>? _completionGate;

        public int ActiveInvocationCountValue { get; set; }

        public TaskCompletionSource DrainStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ActiveInvocationCount => ActiveInvocationCountValue;

        public Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public async Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            operations.Add("await-active-invocations");
            DrainStarted.TrySetResult();

            if (_completionGate is null)
            {
                return true;
            }

            var result = await _completionGate.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            operations.Add("active-invocations-drained");
            ActiveInvocationCountValue = 0;
            return result;
        }

        public Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default)
        {
            return Task.FromResult("{}");
        }

        public void Cancel(Guid invocationId)
        {
        }

        public void CancelDetached(Guid invocationId)
        {
        }

        public void CancelAll()
        {
        }

        public void CleanupStaleToolCalls(TimeSpan maxAge)
        {
        }

        public void ResolveApprovalResult(ApprovalResolvedEvent evt, ApprovalScope scope = ApprovalScope.Once)
        {
        }

        public void ResolveUserQuestionResult(UserQuestionAnsweredEvent evt)
        {
        }

        public void ResolveToolCallResult(ToolCallResultEvent evt)
        {
        }

        public void UseCompletionGate()
        {
            _completionGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void CompleteDrain(bool success)
        {
            _completionGate?.TrySetResult(success);
        }
    }

    private sealed class RecordingDeadLetterStore(OperationLog operations) : IDeadLetterStore
    {
        public List<InvocationFailedPayload> Pending { get; } = [];

        public List<InvocationFailedPayload> Enqueued { get; } = [];

        public List<Guid> Removed { get; } = [];

        public Task EnqueueAsync(InvocationFailedPayload payload, CancellationToken cancellationToken = default)
        {
            Enqueued.Add(payload);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<InvocationFailedPayload>> GetPendingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<InvocationFailedPayload>>(Pending.ToArray());
        }

        public Task RemoveAsync(Guid invocationId, CancellationToken cancellationToken = default)
        {
            operations.Add("remove-dead-letter");
            Removed.Add(invocationId);
            Pending.RemoveAll(entry => entry.InvocationId == invocationId);
            return Task.CompletedTask;
        }

        public long GetCurrentSizeBytes()
        {
            return Pending.Count;
        }
    }

    private sealed class RecordingWorkerHubConnection(OperationLog operations) : IWorkerHubConnection
    {
        public int DisconnectAsyncCallCount { get; private set; }

        public bool BlockUntilCancelled { get; set; }

        public WorkerConnectionState State => WorkerConnectionState.Disconnected;

        event EventHandler<WorkerConnectionStateChangedEventArgs>? IWorkerHubConnection.StateChanged
        {
            add => _ = value;
            remove => _ = value;
        }

        event EventHandler<InvocationAssignedReceivedEventArgs>? IWorkerHubConnection.InvocationAssignedReceived
        {
            add => _ = value;
            remove => _ = value;
        }

        event EventHandler<ToolCallResultReceivedEventArgs>? IWorkerHubConnection.ToolCallResultReceived
        {
            add => _ = value;
            remove => _ = value;
        }

        event EventHandler<DisconnectRequestedReceivedEventArgs>? IWorkerHubConnection.DisconnectRequestedReceived
        {
            add => _ = value;
            remove => _ = value;
        }

        event EventHandler<ApprovalResolvedReceivedEventArgs>? IWorkerHubConnection.ApprovalResolvedReceived
        {
            add => _ = value;
            remove => _ = value;
        }

        event EventHandler<InvocationCancelledReceivedEventArgs>? IWorkerHubConnection.InvocationCancelledReceived
        {
            add => _ = value;
            remove => _ = value;
        }

        event EventHandler<ConversationPurgedReceivedEventArgs>? IWorkerHubConnection.ConversationPurgedReceived
        {
            add => _ = value;
            remove => _ = value;
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectAsyncCallCount++;
            operations.Add("disconnect-worker-hub");
            if (BlockUntilCancelled)
            {
                // Model a hub disconnect that never completes on its own — only the drain's end-to-end deadline unblocks it.
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
        }

        public Task SendWorkerHelloAsync(Guid clientNodeId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendWorkerKeyRegisteredAsync(Guid keyId, string publicKey, string popSignature, string popChallenge, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendCapabilitiesAsync(ClientCapabilities capabilities, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendHeartbeatAsync(Guid clientNodeId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendPurgeConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendInvocationKeyMismatchAsync(Guid messageId, string reason, string nodeKeyIdUsed, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendInvocationAcceptedAsync(Guid invocationId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendEncryptedChunkAsync(EncryptedChunkEnvelopeV1 payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendEncryptedCompletedAsync(EncryptedCompletedEnvelopeV1 payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendEncryptedFailedAsync(EncryptedFailedEnvelopeV1 payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendTokenStreamChunkAsync(Guid invocationId, string token, bool isComplete, long? sourceSequence = null, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendReasoningStreamChunkAsync(Guid invocationId, string token, bool isComplete, long? sourceSequence = null, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendToolCallRequestAsync(ToolCallRequestPayload payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendApprovalRequestAsync(ApprovalRequestPayload payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendInvocationCompletedAsync(InvocationCompletedPayload payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendInvocationFailedAsync(InvocationFailedPayload payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingHubMessageSender(OperationLog operations) : IHubMessageSender
    {
        public bool ThrowOnFailedSend { get; set; }

        public bool BlockUntilCancelled { get; set; }

        public async Task SendInvocationFailedAsync(InvocationFailedPayload payload, CancellationToken cancellationToken = default)
        {
            operations.Add("flush-dead-letter");
            if (ThrowOnFailedSend)
            {
                throw new InvalidOperationException("Simulated dead-letter send failure.");
            }

            if (BlockUntilCancelled)
            {
                // Model a dead-letter resend that never completes on its own — only the drain deadline unblocks it.
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
        }

        public Task SendPurgeConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendInvocationKeyMismatchAsync(Guid messageId, string reason, string nodeKeyIdUsed, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendInvocationAcceptedAsync(Guid invocationId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendEncryptedChunkAsync(EncryptedChunkEnvelopeV1 payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendEncryptedCompletedAsync(EncryptedCompletedEnvelopeV1 payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendEncryptedFailedAsync(EncryptedFailedEnvelopeV1 payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendTokenStreamChunkAsync(Guid invocationId, string token, bool isComplete, long? sourceSequence = null, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendReasoningStreamChunkAsync(Guid invocationId, string token, bool isComplete, long? sourceSequence = null, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendToolCallRequestAsync(ToolCallRequestPayload payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendApprovalRequestAsync(ApprovalRequestPayload payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendInvocationCompletedAsync(InvocationCompletedPayload payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
