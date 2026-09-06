namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;
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
    private readonly FakeIntegrationApiKeyStore _keys = new();
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

    /// <summary>
    ///     <paramref name="maxTrackedExecutions" /> raises the replay ring's capacity for the one suite that seeds more
    ///     rows than its default 64 — a refused ring entry makes the startup sweep leave a row non-terminal on purpose,
    ///     which would be indistinguishable from the paging bug such a suite is there to catch.
    /// </summary>
    public IntegrationCoordinatorHarness(int maxQueueAgeSeconds = 120,
        TimeProvider? timeProvider = null,
        int contextBudgetTokens = 12_000,
        int? maxTrackedExecutions = null)
    {
        MaxQueueAgeSeconds = maxQueueAgeSeconds;

        // A CONSTRUCTOR parameter, not a settable property: the coordinator snapshots IOptions<IntegrationOptions> in
        // its own constructor, so a value assigned afterwards would silently never be read.
        ContextBudgetTokens = contextBudgetTokens;
        Clock = timeProvider ?? TimeProvider.System;
        _reservation = new TrackingDisposable(this);
        _lease = new TrackingAsyncDisposable(this);
        _leaseGate.SetResult();

        _trigger = _triggers.Seed("sensor-ingest", _agentDefinitionId);
        SessionId = Guid.NewGuid();
        ConversationId = Guid.NewGuid();
        _ = _sessions.Seed(SessionId, _trigger.Id, ConversationId, _agentDefinitionId);

        Definitions.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => BuildDefinition());
        NodeSettings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        LocalDefault.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(EffectiveLocalModel);
        Capability.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                  .Returns(new ModelCapabilitySnapshot(SupportsThinking: true, SupportsTools: true, IsCloud: false));
        Resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(_ => new ResolvedAgentRuntime("SCAFFOLD+PERSONA",
                    OfferedTools,
                    ModelProfile: null,
                    "medium",
                    AgentDefinitionVersion: 7,
                    _agentDefinitionId,
                    "Sensor agent",
                    [],
                    Kind: ResolvedKind));
        Capacity.DecideAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    CapacityDecidedOrdinal = Next();
                    return new CapacityDecision(CapacityVerdict.Allow, "Capacity available.", OllamaEvictionWarning: false, _reservation);
                });
        Dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>())
                  .Returns(callInfo => _leaseRequest = AcquireLeaseAsync(callInfo.Arg<CancellationToken>()));
        Persistence.GetConversationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => BuildConversation(capPayloads: false));
        Persistence.GetConversationForTurnAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => BuildConversation(capPayloads: true));
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
                      GenerationDurationMs = 12,
                      TotalTokens = TerminalTotalTokens,
                      // Turn-scoped telemetry the runner reports on every terminal path. Fixed values so the envelope
                      // projection can be asserted: an integration run has to carry them like any other turn.
                      ToolSchemaTokens = 4_096L,
                      MaxToolSchemaTokens = 2_048,
                      ModelReadinessMs = 178_576L,
                      TurnInputTokens = 6_000,
                      TurnOutputTokens = 60,
                      TurnTotalTokens = 6_078,
                      TurnReasoningTokens = 18
                  }));
                  SignalCancel();
              });

        var options = Options.Create(maxTrackedExecutions is { } maxTracked
            ? new IntegrationOptions
            {
                MaxTrackedExecutions = maxTracked
            }
            : new IntegrationOptions());
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

        // The per-turn context bound and the session service the coordinator resolves from its run scope. Both are real
        // instances over recording collaborators, because WHICH keep window the fold asks for and WHETHER a
        // per-invocation session is closed are the assertions, not the fact that a mock was called.
        services.AddSingleton<IConversationCompactionService>(Compaction);
        services.AddSingleton<ITokenEstimator>(new HeuristicTokenEstimator(new TokenEstimatorCalibrationStore()));
        services.AddSingleton<IOptions<ConversationCompactionOptions>>(_ => Options.Create(new ConversationCompactionOptions
        {
            RecentMessagesToKeepVerbatim = ChatKeepVerbatim
        }));
        // The historical tool-result excerpt cap a caller-managed continuation replays under, read from the SAME options
        // the context budgeter measures with so one result truncated twice reads as one result.
        services.AddSingleton<IOptions<ConversationContextBudgetOptions>>(_ => Options.Create(new ConversationContextBudgetOptions()));
        services.AddSingleton(_ => new ConversationStepContextBound(Persistence,
            Compaction,
            new HeuristicTokenEstimator(new TokenEstimatorCalibrationStore()),
            NullLogger<ConversationStepContextBound>.Instance));
        // A REAL offer provider and a settable approval policy: WHICH tools the coordinator unions in, and what the
        // node policy then does to their approval flag, are the assertions.
        services.AddSingleton<ILocalToolOfferProvider>(IntegrationToolOfferFactory.Create());
        services.AddSingleton(FenceSeeds);
        services.AddSingleton<IToolApprovalPolicy>(_ => ToolApprovalPolicy);
        services.AddSingleton<IIntegrationApiKeyStore>(_keys);
        services.AddSingleton(_ => new IntegrationSessionService(_sessions,
            Executions,
            _triggers,
            new IntegrationExternalAccess(Executions, _sessions, _keys),
            Persistence,
            SessionGate,
            TimeProvider.System,
            NullLogger<IntegrationSessionService>.Instance));
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

    /// <summary>The chat keep window the integration path must pass, rather than the work-session floor of two.</summary>
    public const int ChatKeepVerbatim = 8;

    /// <summary>Records every fold the per-turn bound asked for, including the keep window it carried.</summary>
    public RecordingCompactionService Compaction { get; } = new();

    public IntegrationSessionGate SessionGate { get; } = new();

    /// <summary>
    ///     The prior-outputs fence seed. Fixed rather than derived from a node key, so the fenced block is byte-stable
    ///     across runs of one test and a suite can assert on its markers.
    /// </summary>
    public IUntrustedContentFenceSeedProvider FenceSeeds { get; } = BuildFenceSeeds();

    private static IUntrustedContentFenceSeedProvider BuildFenceSeeds()
    {
        var provider = Substitute.For<IUntrustedContentFenceSeedProvider>();
        _ = provider.DeriveSeed(Arg.Any<Guid>()).Returns("integration-harness-fence-seed");
        return provider;
    }

    /// <summary>
    ///     The node's approval policy. Permissive by default — the identity compose, so the default path is unchanged —
    ///     and swappable for the fail-closed case where an operator tightens ReadLocal.
    /// </summary>
    public IToolApprovalPolicy ToolApprovalPolicy { get; set; } = new PermissiveToolApprovalPolicy();

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

    /// <summary>
    ///     The target definition's execution shape. Settable because ruling D2 limits an integration trigger to a
    ///     SINGLE agent, and the coordinator has to refuse an orchestrator that was repointed after the trigger was
    ///     saved.
    /// </summary>
    public AgentDefinitionKind DefinitionKind { get; set; } = AgentDefinitionKind.Single;

    /// <summary>
    ///     The kind the RESOLVER reports, which is a second read of the same definition and can therefore disagree
    ///     with <see cref="DefinitionKind" />: that disagreement is the time-of-check/time-of-use window an operator
    ///     opens by switching the definition to an orchestrator between the two reads.
    /// </summary>
    public AgentDefinitionKind ResolvedKind { get; set; } = AgentDefinitionKind.Single;

    public bool HideConversation { get; set; }

    /// <summary>
    ///     Completed turns already in the owned conversation, ahead of the seeds. This is what a CONTINUED session
    ///     looks like: the accept path persisted the current seed before the coordinator ran, so the turn read already
    ///     contains it and the earlier turns sit in front of it.
    /// </summary>
    public List<NodeChatPersistedMessageDto> History { get; } = [];

    /// <summary>The conversation's non-destructive compaction synopsis and the anchor it folds through, when set.</summary>
    public string? CompactionSummary { get; set; }

    public int? CompactionCoversToSequence { get; set; }

    /// <summary>The per-turn compaction budget the coordinator passes. Pass a low one to the constructor to make the fold fire.</summary>
    public int ContextBudgetTokens { get; }

    /// <summary>One completed turn to place ahead of the seed, in the order it is added.</summary>
    public void AddHistory(string role, string content) =>
        History.Add(Message(Guid.NewGuid(), role, content) with
        {
            Sequence = History.Count
        });

    /// <summary>
    ///     One turn ahead of the seed carrying the persisted PARTS and status a real turn has. The continuation suite
    ///     needs both: replayed tool history is read off an assistant row's parts, and a run that failed after a
    ///     completed tool call still has to carry them.
    /// </summary>
    public void AddHistory(string role, string content, IReadOnlyList<NodeChatMessagePart>? parts, string? status = null) =>
        History.Add(Message(Guid.NewGuid(), role, content) with
        {
            Sequence = History.Count,
            Parts = parts,
            Status = status ?? NodeChatMessageStatusValues.Completed
        });

    /// <summary>A tool part as the accumulator persists a COMPLETED one: the requested phase collapsed into its result.</summary>
    public static NodeChatMessagePart CompletedToolPart(string callId,
        string name,
        string? args,
        string? result,
        int sequence = 0,
        bool isError = false) =>
        new(NodeChatMessagePartKinds.Tool,
            sequence,
            Text: null,
            callId,
            name,
            isError ? NodeChatToolPartStates.Failed : NodeChatToolPartStates.Received,
            args,
            result);

    /// <summary>A tool part that never left the requested phase — the shape a continuation must NOT replay.</summary>
    public static NodeChatMessagePart RequestedToolPart(string callId, string name, string? args, int sequence = 0) =>
        new(NodeChatMessagePartKinds.Tool,
            sequence,
            Text: null,
            callId,
            name,
            NodeChatToolPartStates.Waiting,
            args);

    public bool RaiseTerminalState { get; set; } = true;

    public InvocationStatus TerminalStatus { get; set; } = InvocationStatus.Completed;

    public string? TerminalError { get; set; }

    public FailureCategory? TerminalFailureCategory { get; set; }

    /// <summary>The token total the provider reported, or null for one that reported none.</summary>
    public int? TerminalTotalTokens { get; set; }

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
    public Task<IAsyncDisposable> LeaseRequest => _leaseRequest ?? throw new AssertionException("No lease was ever requested.");

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

    /// <summary>Repoints the trigger's session policy, which decides whether the caller-managed tool rule judges it.</summary>
    public void SetSessionPolicy(IntegrationSessionPolicy sessionPolicy) =>
        _triggers.SetSessionPolicy(_trigger.Id, sessionPolicy);

    /// <summary>The owned session as the stores hold it now, so a suite can assert whether a terminal run closed it.</summary>
    public IntegrationSessionSnapshot Session() =>
        _sessions.Rows.Single(row => row.Id == SessionId);

    /// <summary>One offered tool in the resolved runtime, so a suite can give the run a tool of a chosen category.</summary>
    public static AllowedToolDto Tool(string name, ToolCategory category) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = ToolLocation.ClientLocal,
            ParameterSchema = null,
            RequiresApproval = false,
            Category = category
        };

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
                          new HashSet<IntegrationExecutionStatus>
                          {
                              row.Status
                          },
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
            // real-timer: arriving late is the input. Deliberately NOT linked to the deadline token — the lease has to
            // arrive after the coordinator's own deadline elapses, not be cancelled by it, and that deadline is real
            // wall clock inside the coordinator.
            await Task.Delay(LeaseDelay, CancellationToken.None).ConfigureAwait(false);
        }

        LeaseAcquired = true;
        LeaseAcquiredOrdinal = Next();
        return _lease;
    }

    /// <param name="capPayloads">
    ///     Whether to model the load-side cap the SQL turn read applies: with a synopsis in place it selects both
    ///     <c>content</c> AND <c>metadata_json</c> as NULL for every NON-user row at or below the covered sequence, and
    ///     the persisted tool PARTS live in <c>metadata_json</c>. A fake that returned parts on both reads would let a
    ///     continuation test pass against a read production never performs.
    /// </param>
    private NodeChatConversationDto? BuildConversation(bool capPayloads)
    {
        if (HideConversation)
        {
            return null;
        }

        // Ascending sequences, because the context builder orders in ANCHOR space: with every message at 0 the order
        // would be an accident of insertion rather than the conversation's own.
        var seeds = Executions.Rows.Select((row, index) => Message(row.Id, "user", SeedText) with
                              {
                                  Sequence = History.Count + index
                              })
                              .ToArray();
        IReadOnlyList<NodeChatPersistedMessageDto> messages = [.. History, .. seeds];
        if (capPayloads && !string.IsNullOrEmpty(CompactionSummary) && CompactionCoversToSequence is { } coveredSequence)
        {
            messages =
            [
                .. messages.Select(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) || message.Sequence > coveredSequence
                    ? message
                    : message with
                    {
                        Content = string.Empty,
                        Reasoning = null,
                        Model = null,
                        MetadataJson = null,
                        Parts = null
                    })
            ];
        }

        return new NodeChatConversationDto(ConversationId,
            "sensor-ingest",
            UserId: null,
            CreatedAtUtc: 0,
            LastSeenUtc: 0,
            Purged: false,
            messages,
            CompactionSummary: CompactionSummary,
            CompactionSummaryCoversToSequence: CompactionCoversToSequence);
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
            DefinitionKind,
            AllowedToolNames: [],
            new Dictionary<string, bool>(StringComparer.Ordinal),
            OrchestrationTopologyJson: null,
            Version: 7,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0);

    /// <summary>Reads the harness's mutable queue age, so a test can move the deadline after the lease is requested.</summary>
    private sealed class QueueAgeOptions(IntegrationCoordinatorHarness harness) : IOptions<IntegrationOptions>
    {
        public IntegrationOptions Value =>
            new()
            {
                MaxQueueAgeSeconds = harness.MaxQueueAgeSeconds,
                ContextBudgetTokens = harness.ContextBudgetTokens
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

/// <summary>
///     Records every fold the per-turn context bound asked for, including the KEEP WINDOW it carried — which is the
///     whole assertion: an integration turn must pass the chat window, not the work-session floor of two.
/// </summary>
internal sealed class RecordingCompactionService : IConversationCompactionService
{
    public List<(Guid ConversationId, int? KeepVerbatim)> Calls { get; } = [];

    public Task<ConversationCompactionResult> CompactAsync(Guid conversationId,
        string? requestedModel,
        int? recentMessagesToKeepVerbatim,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((conversationId, recentMessagesToKeepVerbatim));
        return Task.FromResult(new ConversationCompactionResult(ConversationCompactionOutcome.NothingToCompact));
    }
}
