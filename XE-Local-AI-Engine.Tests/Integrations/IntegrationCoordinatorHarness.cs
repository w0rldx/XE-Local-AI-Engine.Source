namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The coordinator under test with every collaborator around it: a real <see cref="LocalChatRuntimePackageBuilder" />
///     and a real event buffer, NSubstitute for the rest, and the node's single invocation permit modelled as an actual
///     <see cref="SemaphoreSlim" /> because who waits on it and when is the whole of the queue-age contract.
///     <para>
///         Shared by the coordinator suite and the streaming suite: both drive one real run, and a second copy of this
///         wiring would drift from the first the moment either slice moved.
///     </para>
/// </summary>
internal sealed class IntegrationCoordinatorHarness : IDisposable
{
    public const string EffectiveLocalModel = "local-default-model";

    public const string SeedText = "Summarize the overnight sensor readings.";

    private readonly IntegrationExecutionEventBuffer _buffer;
    private readonly long _constructedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private Task<IAsyncDisposable>? _leaseRequest;
    private readonly Guid _agentDefinitionId = Guid.NewGuid();
    private readonly FakeIntegrationTriggerStore _triggers = new();
    private readonly FakeIntegrationSessionStore _sessions = new();
    private readonly TrackingDisposable _reservation;
    private readonly TrackingAsyncDisposable _lease;

    /// <summary>
    ///     The node's single invocation permit, modelled for real rather than faked away: F1 is entirely about who
    ///     waits on it and when, and a lease every caller gets instantly cannot show that.
    /// </summary>
    private readonly SemaphoreSlim _leaseSlot = new(initialCount: 1, maxCount: 1);

    private readonly ServiceProvider _provider;
    private readonly IntegrationTriggerSnapshot _trigger;
    private TaskCompletionSource _leaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _ordinal;

    public IntegrationCoordinatorHarness(int maxQueueAgeSeconds = 120, TimeProvider? timeProvider = null)
    {
        MaxQueueAgeSeconds = maxQueueAgeSeconds;
        Clock = timeProvider ?? TimeProvider.System;
        _reservation = new TrackingDisposable(this);
        _lease = new TrackingAsyncDisposable(this);
        _leaseGate.SetResult();

        _trigger = _triggers.Seed("sensor-ingest", _agentDefinitionId);
        SessionId = Guid.NewGuid();
        ConversationId = Guid.NewGuid();
        _ = _sessions.Seed(SessionId, _trigger.Id, ConversationId, _agentDefinitionId);

        Definitions.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(BuildDefinition());
        NodeSettings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        LocalDefault.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(EffectiveLocalModel);
        Capability.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                  .Returns(new ModelCapabilitySnapshot(SupportsThinking: true, SupportsTools: true, IsCloud: false));
        Resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(_ => new ResolvedAgentRuntime("SCAFFOLD+PERSONA", OfferedTools, ModelProfile: null, "medium", AgentDefinitionVersion: 7, _agentDefinitionId, "Sensor agent", []));
        Capacity.DecideAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    CapacityDecidedOrdinal = Next();
                    return new CapacityDecision(CapacityVerdict.Allow, "Capacity available.", OllamaEvictionWarning: false, _reservation);
                });
        Dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>())
                  .Returns(callInfo => _leaseRequest = AcquireLeaseAsync(callInfo.Arg<CancellationToken>()));
        Persistence.GetConversationForTurnAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => BuildConversation());
        Persistence.CreateAssistantPlaceholderAsync(Arg.Any<NodeChatCreateAssistantPlaceholderRequest>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo =>
                   {
                       PlaceholderOrdinal = Next();
                       var request = callInfo.Arg<NodeChatCreateAssistantPlaceholderRequest>();
                       return Message(request.MessageId, "assistant", string.Empty);
                   });
        Persistence.TerminalizeAssistantMessageAsync(Arg.Any<NodeChatTerminalizeMessageRequest>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo =>
                   {
                       TerminalizeRequest = callInfo.Arg<NodeChatTerminalizeMessageRequest>();
                       return Message(TerminalizeRequest.Correlation.MessageId, "assistant", TerminalizeRequest.Content ?? string.Empty);
                   });
        UsageProviders.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("llamacpp");
        Runner.When(runner => runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>()))
              .Do(callInfo =>
              {
                  RunOrdinal = Next();
                  RunCount++;
                  var package = callInfo.Arg<InvocationExecutionContext>().Package;
                  CapturedPackage = package;
                  StampStopMarker();

                  // Raised from inside RunAsync on the runner's own thread, which is where the dispatcher raises in
                  // production and therefore the only place a streaming test can observe the handler's real threading.
                  DuringRun?.Invoke(this, package);

                  if (!RaiseTerminalState)
                  {
                      SignalCancel();
                      return;
                  }

                  // Raised from inside RunAsync, exactly as the dispatcher does in production.
                  Dispatcher.InvocationStateChanged += Raise.EventWith(new InvocationStateChangedEventArgs(new InvocationState
                  {
                      InvocationId = package.InvocationId,
                      ConversationId = package.ConversationId,
                      Status = TerminalStatus,
                      Error = TerminalError,
                      FailureCategory = TerminalFailureCategory,
                      ModelUsed = EffectiveLocalModel,
                      StartedAt = DateTimeOffset.UnixEpoch,
                      CompletedAt = DateTimeOffset.UnixEpoch,
                      GenerationDurationMs = 12
                  }));
                  SignalCancel();
              });

        var options = Options.Create(new IntegrationOptions());
        _buffer = new IntegrationExecutionEventBuffer(options, TimeProvider.System);

        var services = new ServiceCollection();
        services.AddSingleton<IIntegrationExecutionStore>(Executions);
        services.AddSingleton<IIntegrationSessionStore>(_sessions);
        services.AddSingleton<IIntegrationTriggerStore>(_triggers);
        services.AddSingleton(Definitions);
        services.AddSingleton(Resolver);
        services.AddSingleton(Capability);
        services.AddSingleton(LocalDefault);
        services.AddSingleton(NodeSettings);
        services.AddSingleton(Capacity);
        services.AddSingleton<ILocalChatRuntimePackageBuilder, LocalChatRuntimePackageBuilder>();
        services.AddSingleton(Runner);
        services.AddSingleton(Dispatcher);
        services.AddSingleton(Persistence);
        services.AddSingleton(AuditLog);
        services.AddSingleton(UsageProviders);
        // The mapper reads its debounce from the SAME options the chat pump uses, so the run scope must carry them.
        services.AddSingleton<IOptions<ChatStreamBudgetOptions>>(_ => Options.Create(new ChatStreamBudgetOptions
        {
            EmitDebounceMs = EmitDebounceMs
        }));
        _provider = services.BuildServiceProvider();

        Queue = Channel.CreateBounded<Guid>(8);
        Coordinator = new IntegrationExecutionCoordinator(_provider.GetRequiredService<IServiceScopeFactory>(),
            Queue,
            _buffer,
            Cancellations,
            new QueueAgeOptions(this),
            Clock,
            NullLogger<IntegrationExecutionCoordinator>.Instance);
    }

    public IntegrationExecutionCoordinator Coordinator { get; }

    /// <summary>The coordinator's own queue, so a test can drive the hosted loop instead of calling into it.</summary>
    public Channel<Guid> Queue { get; }

    public FakeIntegrationExecutionStore Executions { get; } = new();

    public IntegrationCancellationRegistry Cancellations { get; } = new();

    public IIntegrationExecutionEventBuffer Buffer => _buffer;

    public IAgentDefinitionStore Definitions { get; } = Substitute.For<IAgentDefinitionStore>();

    public IAgentDefinitionResolver Resolver { get; } = Substitute.For<IAgentDefinitionResolver>();

    public IModelCapabilityResolver Capability { get; } = Substitute.For<IModelCapabilityResolver>();

    public ILocalDefaultChatModelResolver LocalDefault { get; } = Substitute.For<ILocalDefaultChatModelResolver>();

    public INodeSettingsStore NodeSettings { get; } = Substitute.For<INodeSettingsStore>();

    public ICapacityService Capacity { get; } = Substitute.For<ICapacityService>();

    public IInvocationRunner Runner { get; } = Substitute.For<IInvocationRunner>();

    public IWorkerEventDispatcher Dispatcher { get; } = Substitute.For<IWorkerEventDispatcher>();

    public INodeChatPersistenceService Persistence { get; } = Substitute.For<INodeChatPersistenceService>();

    public IAgentExecutionLogStore AuditLog { get; } = Substitute.For<IAgentExecutionLogStore>();

    public IUsageProviderResolver UsageProviders { get; } = Substitute.For<IUsageProviderResolver>();

    public Guid SessionId { get; }

    public Guid ConversationId { get; }

    public int MaxQueueAgeSeconds { get; }

    public TimeSpan LeaseDelay { get; set; } = TimeSpan.Zero;

    /// <summary>The clock the coordinator and its stream mapper share, so a debounce window is a test move rather than a wait.</summary>
    public TimeProvider Clock { get; }

    /// <summary>The mapper's coalescing window, read into the run scope's <see cref="ChatStreamBudgetOptions" />.</summary>
    public int EmitDebounceMs { get; init; } = 40;

    /// <summary>Runs on the runner's thread inside <c>RunAsync</c>, so a test can raise dispatcher events mid-run.</summary>
    public Action<IntegrationCoordinatorHarness, RuntimePackage>? DuringRun { get; set; }

    public IReadOnlyList<AllowedToolDto> OfferedTools { get; set; } = [];

    public bool HideConversation { get; set; }

    public bool RaiseTerminalState { get; set; } = true;

    public InvocationStatus TerminalStatus { get; set; } = InvocationStatus.Completed;

    public string? TerminalError { get; set; }

    public FailureCategory? TerminalFailureCategory { get; set; }

    public RuntimePackage? CapturedPackage { get; private set; }

    public NodeChatTerminalizeMessageRequest? TerminalizeRequest { get; private set; }

    public int RunCount { get; private set; }

    public int RunOrdinal { get; private set; }

    public int PlaceholderOrdinal { get; private set; }

    public int CapacityDecidedOrdinal { get; private set; }

    public int LeaseAcquiredOrdinal { get; private set; }

    public int LeaseDisposedOrdinal { get; private set; }

    public int ReservationDisposedOrdinal { get; private set; }

    public bool LeaseRequested { get; private set; }

    public bool LeaseAcquired { get; private set; }

    public bool LeaseDisposed => LeaseDisposedOrdinal > 0;

    /// <summary>The in-flight lease request, so a test can settle it instead of guessing at a delay.</summary>
    public Task<IAsyncDisposable> LeaseRequest =>
        _leaseRequest ?? throw new AssertionException("No lease was ever requested.");

    /// <summary>Stamps a durable stop marker on this row from inside the run, which bumps the version the terminal CAS carries.</summary>
    public Guid? StampStopMarkerFor { get; set; }

    /// <summary>Signals the registry's cancel token from inside the run, exactly as the cancel primitive does.</summary>
    public Guid? SignalCancelFor { get; set; }

    public Guid SeedAccepted(IntegrationExecutionStatus status = IntegrationExecutionStatus.Accepted,
        long? receivedAtUtc = null,
        long lastSequence = 1,
        long version = 0,
        long? stopRequestedAtUtc = null)
    {
        var executionId = Guid.NewGuid();
        _ = Executions.Seed(executionId,
            _trigger.Id,
            SessionId,
            status,
            // A second BEFORE this harness built the coordinator, so the default row reads as one the previous
            // process left behind and the startup sweep still visits it.
            receivedAtUtc ?? (_constructedAtUtc - 1_000),
            lastSequence,
            version,
            stopRequestedAtUtc);
        _ = _buffer.TryCreate(executionId, lastSequence);
        return executionId;
    }

    public IntegrationExecutionSnapshot Row(Guid executionId) =>
        Executions.Rows.Single(row => row.Id == executionId);

    /// <summary>
    ///     A row admitted AFTER this coordinator was constructed, so the startup sweep leaves it alone and the
    ///     hosted loop is the only thing that touches it.
    /// </summary>
    public Guid SeedLive() =>
        SeedAccepted(receivedAtUtc: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1_000);

    public void DisableTrigger() =>
        _triggers.Disable(_trigger.Id);

    public void HoldLease() =>
        _leaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseLease() =>
        _leaseGate.TrySetResult();

    /// <summary>Takes the node's one permit on behalf of something else — a chat turn, a scheduled run.</summary>
    public void HoldLeaseSlot() =>
        _leaseSlot.Wait();

    public void ReleaseLeaseSlot() =>
        _ = _leaseSlot.Release();

    public void Dispose()
    {
        _leaseSlot.Dispose();
        _buffer.Dispose();
        _reservation.Dispose();
        _provider.Dispose();
    }

    internal int Next() =>
        Interlocked.Increment(ref _ordinal);

    internal void RecordLeaseDisposed() =>
        LeaseDisposedOrdinal = Next();

    internal void RecordReservationDisposed()
    {
        ReservationDisposedOrdinal = Next();
        if (!FailNextReservationDispose)
        {
            return;
        }

        FailNextReservationDispose = false;
        throw new InvalidOperationException("The capacity reservation could not be released.");
    }

    /// <summary>Makes the next reservation disposal throw, which must NOT cost the node its invocation lease.</summary>
    public bool FailNextReservationDispose { get; set; }

    private void StampStopMarker()
    {
        if (StampStopMarkerFor is not { } target)
        {
            return;
        }

        StampStopMarkerFor = null;

        // Exactly what the cancel primitive's step 1 does to a Running row: a pure marker write under the row's
        // CURRENT version, which bumps it and leaves the coordinator's terminal CAS holding a stale one.
        var row = Executions.Rows.Single(candidate => candidate.Id == target);
        _ = Executions.UpdateStatusAsync(new IntegrationExecutionStatusUpdate(target,
                                 row.Version,
                                 new HashSet<IntegrationExecutionStatus> { row.Status },
                                 row.Status,
                                 StartedAtUtc: null,
                                 EndedAtUtc: null,
                                 InvocationId: null,
                                 StopRequestedAtUtc: 4_242))
                      .GetAwaiter()
                      .GetResult();
    }

    private void SignalCancel()
    {
        if (SignalCancelFor is not { } target)
        {
            return;
        }

        SignalCancelFor = null;
        _ = Cancellations.Signal(target);
    }

    private async Task<IAsyncDisposable> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        LeaseRequested = true;
        await _leaseGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        // The permit wait is the cancellable part, exactly as the dispatcher's SemaphoreSlim is.
        await _leaseSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (LeaseDelay > TimeSpan.Zero)
        {
            // Deliberately NOT linked to the deadline token: the lease has to arrive LATE, not be cancelled.
            await Task.Delay(LeaseDelay, CancellationToken.None).ConfigureAwait(false);
        }

        LeaseAcquired = true;
        LeaseAcquiredOrdinal = Next();
        return _lease;
    }

    private NodeChatConversationDto? BuildConversation()
    {
        if (HideConversation)
        {
            return null;
        }

        var seeds = Executions.Rows.Select(row => Message(row.Id, "user", SeedText)).ToArray();
        return new NodeChatConversationDto(ConversationId,
            "sensor-ingest",
            UserId: null,
            CreatedAtUtc: 0,
            LastSeenUtc: 0,
            Purged: false,
            seeds);
    }

    private static NodeChatPersistedMessageDto Message(Guid messageId, string role, string content) =>
        new(messageId,
            Guid.Empty,
            RequestId: null,
            Sequence: 0,
            role,
            content,
            Reasoning: null,
            NodeChatMessageStatusValues.Completed,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0,
            Model: null,
            Error: null,
            MetadataJson: null);

    private AgentDefinitionRecord BuildDefinition() =>
        new(_agentDefinitionId,
            "Sensor agent",
            Description: null,
            Instructions: "raw instructions (must NOT be used directly)",
            ModelProfile: null,
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            AllowedToolNames: [],
            new Dictionary<string, bool>(StringComparer.Ordinal),
            OrchestrationTopologyJson: null,
            Version: 7,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0);

    /// <summary>Reads the harness's mutable queue age, so a test can move the deadline after the lease is requested.</summary>
    private sealed class QueueAgeOptions(IntegrationCoordinatorHarness harness) : IOptions<IntegrationOptions>
    {
        public IntegrationOptions Value => new()
        {
            MaxQueueAgeSeconds = harness.MaxQueueAgeSeconds
        };
    }

    private sealed class TrackingDisposable(IntegrationCoordinatorHarness harness) : IDisposable
    {
        public void Dispose() =>
            harness.RecordReservationDisposed();
    }

    private sealed class TrackingAsyncDisposable(IntegrationCoordinatorHarness harness) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            harness.RecordLeaseDisposed();
            harness.ReleaseLeaseSlot();
            return ValueTask.CompletedTask;
        }
    }
}
