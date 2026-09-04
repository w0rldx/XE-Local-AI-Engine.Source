namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     The single consumer of the accept path's queue, and the only component that runs an integration execution.
///     <para>
///         It drives the SAME seam the scheduler's <c>run-agent</c> template uses —
///         <see cref="IAgentDefinitionResolver" /> + <see cref="ILocalChatRuntimePackageBuilder" /> +
///         <see cref="InvocationExecutionContext.CreatePlain" /> + <see cref="IInvocationRunner" /> — with
///         <c>IsUnattended: true</c>, serialised behind the node's single invocation lease. It introduces no second
///         runtime path, and it is the ONLY producer of a terminal event for an execution.
///     </para>
///     <para>
///         <b>Three deliberate divergences from the scheduler.</b> (1) The invocation lease is taken BEFORE the
///         capacity reservation, so a queued run does not hold a GPU footprint across the whole wait and fail a
///         concurrent interactive turn's capacity decision. (2) Approval-required tools are NOT stripped from the
///         offer: an external caller cannot see a silently degraded agent, so the run fails loudly and audited
///         instead. (3) The wait for the lease runs under a queue-age deadline, so an advertised maximum queue age
///         bounds something a caller can observe.
///     </para>
/// </summary>
internal sealed class IntegrationExecutionCoordinator : BackgroundService
{
    /// <summary>How many rows the startup sweep pulls per page. Bounded so a large backlog does not load in one list.</summary>
    private const int RecoveryPageSize = 200;

    /// <summary>The statuses a terminal transition may leave. Nothing else is a legal source for one.</summary>
    private static readonly IReadOnlySet<IntegrationExecutionStatus> NonTerminalStatuses = new HashSet<IntegrationExecutionStatus>
    {
        IntegrationExecutionStatus.Accepted,
        IntegrationExecutionStatus.Queued,
        IntegrationExecutionStatus.Running
    };

    private static readonly IReadOnlySet<IntegrationExecutionStatus> BeforeRunStatuses = new HashSet<IntegrationExecutionStatus>
    {
        IntegrationExecutionStatus.Accepted,
        IntegrationExecutionStatus.Queued
    };

    private static readonly IReadOnlySet<IntegrationExecutionStatus> AcceptedOnly = new HashSet<IntegrationExecutionStatus>
    {
        IntegrationExecutionStatus.Accepted
    };

    private static readonly IReadOnlySet<IntegrationExecutionStatus> RunningOnly = new HashSet<IntegrationExecutionStatus>
    {
        IntegrationExecutionStatus.Running
    };

    private readonly IIntegrationExecutionEventBuffer _buffer;
    private readonly IntegrationCancellationRegistry _cancellations;
    private readonly ILogger<IntegrationExecutionCoordinator> _logger;
    private readonly IntegrationOptions _options;
    private readonly Channel<Guid> _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    ///     When this instance was built, which is during host construction and therefore before ANY hosted service —
    ///     Kestrel's included — has started. The startup sweep uses it instead of assuming a registration order it does
    ///     not control: the listener may already be accepting requests while the sweep pages, and a row admitted in
    ///     that window holds a 202 the caller has been given.
    /// </summary>
    private readonly long _constructedAtUtc;

    public IntegrationExecutionCoordinator(IServiceScopeFactory scopeFactory,
        Channel<Guid> queue,
        IIntegrationExecutionEventBuffer buffer,
        IntegrationCancellationRegistry cancellations,
        IOptions<IntegrationOptions> options,
        TimeProvider timeProvider,
        ILogger<IntegrationExecutionCoordinator> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _cancellations = cancellations ?? throw new ArgumentNullException(nameof(cancellations));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _constructedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }

    /// <summary>
    ///     Reconciles every row this process cannot resume BEFORE the consumer loop starts, so the loop can never read
    ///     an id the sweep has not visited yet. There is exactly ONE sweep: because admission commits before the owned
    ///     conversation is created, no orphan conversation can exist and there is nothing else to reclaim.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReconcileInterruptedAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Runs one execution to a terminal row. The tests drive this directly rather than starting the hosted loop and
    ///     writing to the channel, which would make every assertion a race.
    /// </summary>
    internal async Task ProcessOneAsync(Guid executionId, CancellationToken stoppingToken)
    {
        // Registered BEFORE the first await: a cancel arriving between the channel read and the lease request has to
        // find a handle, or a queued row would sit in the lease wait until the lease came free on its own.
        if (!_cancellations.TryRegister(executionId, out var cancelToken))
        {
            _logger.LogWarning("Integration execution {ExecutionId} is already being processed; ignoring the duplicate queue entry.", executionId);
            return;
        }

        try
        {
            using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cancelToken);
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IIntegrationExecutionStore>();

            var execution = await store.GetByIdAsync(executionId, CancellationToken.None).ConfigureAwait(false);
            if (execution is null)
            {
                _logger.LogWarning("Integration execution {ExecutionId} was queued but no row exists for it.", executionId);
                return;
            }

            if (!NonTerminalStatuses.Contains(execution.Status))
            {
                // Already terminalized elsewhere — the cancel path reached it first. Whoever won the terminal CAS owns
                // the terminal event and the audit row, so this one appends nothing.
                return;
            }

            var context = new ExecutionRunContext(store,
                scope.ServiceProvider.GetRequiredService<IAgentExecutionLogStore>(),
                execution);

            try
            {
                await ExecuteOneAsync(scope.ServiceProvider, context, execution, runCancellation.Token, cancelToken, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Every stage is wrapped so a throw still terminalizes: a row stuck Running is worse than a wrong one,
                // because the admission count holds a slot against it forever.
                var shutdown = stoppingToken.IsCancellationRequested && exception is OperationCanceledException;
                _logger.LogError(exception, "Integration execution {ExecutionId} faulted; terminalizing it.", executionId);
                await TerminalizeFromFaultAsync(context,
                        shutdown ? IntegrationFailureCategories.Shutdown : IntegrationFailureCategories.InternalFailure,
                        shutdown ? "The node stopped while the execution was in flight." : "The execution failed unexpectedly.")
                    .ConfigureAwait(false);
            }

            // Placed AFTER every terminal path rather than inside one of them: an execution can end at a dozen points,
            // including the fault handler above, and a per-invocation session left Active by any of them would stay
            // that way forever.
            await ClosePerInvocationSessionAsync(scope.ServiceProvider, execution).ConfigureAwait(false);
        }
        finally
        {
            _cancellations.Remove(executionId);
        }
    }

    /// <summary>
    ///     ONE channel reader, but processing is NOT serialised on it. Awaiting <see cref="ProcessOneAsync" /> here kept
    ///     the next id in the channel for the whole of the current run, and every deadline control — the queue-age
    ///     pre-check, the <c>CancelAfter</c> on the lease wait, the post-acquisition re-check — plus the sole writer of
    ///     <c>Queued</c> live inside that method. A second execution therefore never reached <c>Queued</c>, never
    ///     started measuring its queue age, and only timed out once the run ahead of it finished: R5-2's bound
    ///     measured the wrong thing.
    ///     <para>
    ///         Dispatching instead lets every admitted execution wait on the node's invocation lease itself, which is
    ///         what serialises the runs — a <see cref="SemaphoreSlim" /> granting in wait order. The number of live
    ///         tasks is bounded by the admission cap, because each one holds a non-terminal row against it.
    ///     </para>
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Touched only by this loop, so no lock: pruning on every dispatch keeps it at the live-task count.
        var inFlight = new List<Task>();
        try
        {
            // Queued ids left behind when the token trips are simply dropped: their rows are still Accepted, and the
            // next StartAsync sweep flips them to Failed / restart.
            while (await _queue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                while (!stoppingToken.IsCancellationRequested && _queue.Reader.TryRead(out var executionId))
                {
                    _ = inFlight.RemoveAll(static task => task.IsCompleted);
                    inFlight.Add(RunDispatchedAsync(executionId, stoppingToken));
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Ordinary shutdown.
        }

        // Every dispatched run terminalizes itself on the way down (`shutdown`), so the host must not tear their DI
        // scopes away mid-write. RunDispatchedAsync never faults, so this cannot throw.
        await Task.WhenAll(inFlight).ConfigureAwait(false);
    }

    /// <summary>
    ///     Runs one dispatched execution to completion. <see cref="ProcessOneAsync" /> terminalizes its own faults;
    ///     anything that still escapes it — a store read that throws before the run's own handler is in scope — must
    ///     not take the reader loop, and with it every later execution on this node, down with it.
    /// </summary>
    private async Task RunDispatchedAsync(Guid executionId, CancellationToken stoppingToken)
    {
        try
        {
            await ProcessOneAsync(executionId, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Integration execution {ExecutionId} faulted outside its own handler; its row is left for the next restart sweep.", executionId);
        }
    }

    /// <summary>
    ///     Fails every row this process cannot resume. V1 does not resume in-flight generations, so an interrupted row
    ///     becomes <c>Failed</c> / <c>restart</c> with its terminal event minted through the buffer at
    ///     <c>LastSequence + 1</c> — the number the retired hand-computed carve-out produced, now from the one authority.
    /// </summary>
    private async Task ReconcileInterruptedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IIntegrationExecutionStore>();
            var auditLog = scope.ServiceProvider.GetRequiredService<IAgentExecutionLogStore>();

            var interrupted = new List<IntegrationExecutionSnapshot>();
            foreach (var status in NonTerminalStatuses)
            {
                var offset = 0;
                while (true)
                {
                    var page = await store.ListAsync(new IntegrationExecutionFilter(TriggerId: null, SessionId: null, status, RecoveryPageSize, offset), cancellationToken)
                                          .ConfigureAwait(false);
                    interrupted.AddRange(page);
                    if (page.Count < RecoveryPageSize)
                    {
                        break;
                    }

                    offset += page.Count;
                }
            }

            var recovered = 0;
            foreach (var row in interrupted)
            {
                if (row.ReceivedAtUtc >= _constructedAtUtc)
                {
                    // Admitted after this coordinator existed, so it cannot be a leftover of the previous process: the
                    // accept path enqueues every row it commits, and this one's caller is holding its 202.
                    continue;
                }

                // R3-1: seed the ring from the persisted watermark so the sweep's terminal event continues the
                // execution's OWN numbering instead of restarting at 1 and colliding with rows already written.
                if (!_buffer.TryCreate(row.Id, row.LastSequence))
                {
                    _logger.LogWarning("The event buffer refused a recovery entry for integration execution {ExecutionId}; it stays non-terminal for the next restart.", row.Id);
                    continue;
                }

                var context = new ExecutionRunContext(store, auditLog, row);
                if (await TerminalizeAsync(context,
                            NonTerminalStatuses,
                            IntegrationExecutionStatus.Failed,
                            IntegrationFailureCategories.Restart,
                            "The node restarted while the execution was in flight.")
                        .ConfigureAwait(false))
                {
                    recovered++;

                    // The sweep is a DIFFERENT terminal path from the run's own, and it has to close per-invocation
                    // sessions too — otherwise a session interrupted by a restart stays Active with no execution that
                    // could ever close it. The busy guard is bypassed by construction: the row is already terminal.
                    await ClosePerInvocationSessionAsync(scope.ServiceProvider, row).ConfigureAwait(false);
                }
            }

            if (recovered > 0)
            {
                _logger.LogWarning("Terminalized {Count} interrupted integration execution(s) during startup recovery.", recovered);
            }
        }
        catch (Exception exception)
        {
            // A failed sweep leaves the rows non-terminal for the next restart to retry. Refusing to start the node
            // over it would trade a bounded, self-healing backlog for an outage.
            _logger.LogError(exception, "Integration execution startup recovery failed; interrupted rows stay non-terminal.");
        }
    }

    private async Task ExecuteOneAsync(IServiceProvider services,
        ExecutionRunContext context,
        IntegrationExecutionSnapshot execution,
        CancellationToken runToken,
        CancellationToken cancelToken,
        CancellationToken stoppingToken)
    {
        var executionId = execution.Id;

        // 1. Everything the run needs. A missing conversation or seed is the ONE shape R4-1's forward-running failure
        //    leaves behind: the execution row committed before they were written, so the row is real and has nothing
        //    to run. Do not repair it — the seed text is not recoverable from the row, and a run against an empty seed
        //    is a worse outcome than a clean failure.
        var sessions = services.GetRequiredService<IIntegrationSessionStore>();
        var session = await sessions.GetByIdAsync(execution.SessionId, runToken).ConfigureAwait(false);
        if (session is null)
        {
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.InternalFailure, "The execution's session row is missing.").ConfigureAwait(false);
            return;
        }

        var trigger = await services.GetRequiredService<IIntegrationTriggerStore>().GetByIdAsync(execution.TriggerId, runToken).ConfigureAwait(false);
        if (trigger is null || !trigger.Enabled)
        {
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.TriggerUnavailable, "The trigger was removed or disabled before the execution ran.").ConfigureAwait(false);
            return;
        }

        context.Describe(trigger.Name, trigger.TargetAgentDefinitionId);

        var definition = await services.GetRequiredService<IAgentDefinitionStore>().GetByIdAsync(trigger.TargetAgentDefinitionId, runToken).ConfigureAwait(false);
        if (definition is null)
        {
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.TriggerUnavailable, "The trigger's target agent no longer exists.").ConfigureAwait(false);
            return;
        }

        var persistence = services.GetRequiredService<INodeChatPersistenceService>();

        // 2. A cancel that landed before this row was picked up. A row that already READS Cancelled was terminalized by
        //    the cancel path, which owns both artefacts; this one appends nothing.
        if (execution.Status == IntegrationExecutionStatus.Cancelled)
        {
            return;
        }

        if (execution.StopRequestedAtUtc is not null)
        {
            await TerminalizeAsync(context, BeforeRunStatuses, IntegrationExecutionStatus.Cancelled, failureCategory: null, failureSummary: null).ConfigureAwait(false);
            return;
        }

        // 3. The effective model, and the locality gate. A cloud model is rejected UP FRONT, before the lease and
        //    before the capacity decision, so unattended external work never egresses. The capacity decision itself
        //    moves to step 7, after the lease.
        var nodeSettings = await services.GetRequiredService<INodeSettingsStore>().LoadAsync(runToken).ConfigureAwait(false);
        var localDefaultModel = await services.GetRequiredService<ILocalDefaultChatModelResolver>()
                                              .ResolveAsync(nodeSettings.DefaultModelName, runToken)
                                              .ConfigureAwait(false);
        var pinnedModel = string.IsNullOrWhiteSpace(definition.ModelProfile) ? null : definition.ModelProfile;
        var effectiveModel = pinnedModel ?? localDefaultModel;
        if (string.IsNullOrWhiteSpace(effectiveModel))
        {
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.TriggerUnavailable, "No local chat model is available to run the trigger's agent.").ConfigureAwait(false);
            return;
        }

        var capabilities = await services.GetRequiredService<IModelCapabilityResolver>().ResolveAsync(effectiveModel, runToken).ConfigureAwait(false);
        var (supportsThinking, supportsTools, effectiveModelIsCloud) = capabilities;
        if (effectiveModelIsCloud)
        {
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.CloudModelRejected, "The trigger's effective model is cloud-hosted, and unattended runs are node-local only.").ConfigureAwait(false);
            return;
        }

        // 3b. The compaction bound, BEFORE the conversation is read so the read sees the folded transcript. It projects
        //     what the next turn would replay and folds only when that is over budget; every no-op outcome (no local
        //     model, nothing foldable) is non-fatal by design.
        //
        //     The keep window is the CHAT window, not the work-session floor of two: a work-session step rebuilds its
        //     state block from the database every step, so its transcript beyond the previous step is expendable. An
        //     integration session has no state block — its transcript IS the session state — so folding to two would
        //     delete the continuation a caller-managed session exists to deliver. It is read from the chat compaction
        //     options rather than written as a literal, so an operator who retunes chat retunes this too.
        await services.GetRequiredService<WorkSessionStepContextBound>()
                      .ApplyAsync(session.ConversationId,
                          _options.ContextBudgetTokens,
                          effectiveModel,
                          runToken,
                          services.GetRequiredService<IOptions<ConversationCompactionOptions>>().Value.RecentMessagesToKeepVerbatim)
                      .ConfigureAwait(false);

        // 3c. The turn read, and the ONE shape R4-1's forward-running failure leaves behind: the execution row commits
        //     before the conversation and the seed are written, so a row can be real and have nothing to run. Do not
        //     repair it — the seed text is not recoverable from the row, and a run against an empty seed is a worse
        //     outcome than a clean failure.
        var conversation = await persistence.GetConversationForTurnAsync(session.ConversationId, runToken).ConfigureAwait(false);
        var seed = conversation?.Messages.FirstOrDefault(message => message.MessageId == executionId);
        if (conversation is null || seed is null)
        {
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.InternalFailure, "The execution's owned conversation or seed turn is missing.").ConfigureAwait(false);
            return;
        }

        // 4. The agent's COMPLETE resolved runtime — never the raw definition instructions.
        var resolved = await services.GetRequiredService<IAgentDefinitionResolver>()
                                     .ResolveAsync(definition.Id,
                                         effectiveModel,
                                         seed.Content,
                                         supportsTools,
                                         honorModelProfile: true,
                                         effectiveModelIsCloud,
                                         runToken)
                                     .ConfigureAwait(false);
        if (resolved is null)
        {
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.TriggerUnavailable, "The trigger's target agent no longer exists.").ConfigureAwait(false);
            return;
        }

        // 4b. Ruling R4-9(a), judged against the offer this package will actually carry rather than a second resolve
        //     that could disagree. The trigger was checked at save, but an agent definition's tools can change
        //     afterwards, and this is the last point that sees them as they are now. A caller-managed session persists
        //     the seed and the final assistant text and nothing else, so a continued run cannot tell an action it
        //     already performed from prose describing one — safe only while the agent can perform none.
        if (trigger.SessionPolicy == IntegrationSessionPolicy.CallerManaged
            && !IIntegrationTriggerService.AllowsCallerManaged(resolved.AllowedTools))
        {
            await TerminalizeBeforeRunAsync(context,
                    IntegrationFailureCategories.SessionPolicy,
                    "The trigger's agent now offers a tool outside ToolCategory.ReadLocal, which a caller-managed session cannot host.")
                .ConfigureAwait(false);
            return;
        }

        // 4c. The turn's context, assembled by the SAME builder the chat send path uses, so a continued session replays
        //     exactly as a conversation does. The seed is LIFTED OUT of the history it is already in: the accept path
        //     persisted it before this coordinator ran, unlike the chat path where the read precedes the write, so
        //     concatenating it again would send the caller's input twice.
        //
        //     selectedPath is always null — integration conversations never regenerate, so there are no variant groups.
        //     imageContext and knowledgeContext take their defaults: an integration execution has neither.
        var history = conversation with
        {
            Messages = [.. conversation.Messages.Where(message => message.MessageId != executionId)]
        };
        //     A CALLER-MANAGED continuation additionally carries one framed document replaying the session's committed
        //     external.output payloads, in the builder's existing attachmentContext slot — so it lands at slot 0, ahead
        //     of the synopsis and the verbatim turns, which is the same placement and the same reason an uploaded
        //     attachment gets: it is reference material to read BEFORE the recent turns, not a turn of its own.
        var priorOutputs = trigger.SessionPolicy == IntegrationSessionPolicy.CallerManaged
            ? await BuildPriorOutputsAsync(services, session, executionId, runToken).ConfigureAwait(false)
            : null;

        var conversationContext = ConversationContextBuilder.Build(history,
            seed,
            selectedPath: null,
            priorOutputs);

        // 5. The headless package. Three things differ from the scheduler's: the conversation id is the OWNED one (a
        //    throwaway Guid would break every by-conversation resolution downstream), the context is the session's
        //    history through the same compaction splice chat uses, and AllowedTools is passed THROUGH UNCHANGED.
        //
        //    Approval-required tools are deliberately not stripped. For a scheduled run the scheduler's strip logs a
        //    warning an operator eventually reads; for an external integration it is silent degradation — the caller
        //    gets a plausible Completed from an agent that quietly lost a capability its configuration says it has, and
        //    neither the caller nor the response can tell. Left in the offer, ToolApprovalCoordinator sees IsUnattended,
        //    writes its unattended-unavailable audit row and fails the run by name.
        // 4d. emit_output, unioned in AFTER the definition's offer ∩ AllowedToolNames intersection and BEFORE the agent
        //     is constructed — the exact seam ask_user uses, and for the same reason: delivering a result to the caller
        //     that started this run is a property of running an integration execution, not a per-agent permission.
        //
        //     The approval flag is recomposed through the node policy HERE, because this is the only place it can be:
        //     the offer provider hands out the raw declared flag and consults no policy, and InvocationToolResolver
        //     reads the flag the offer carries rather than asking the policy itself. So a node that requires approval
        //     for ReadLocal tightens this tool too, and the run then fails CLOSED at the first call — an operator who
        //     declares that ReadLocal needs a human cannot be handed a silent exception on the one surface reachable
        //     from outside the node.
        var approvalPolicy = services.GetRequiredService<IToolApprovalPolicy>();
        var offerProvider = services.GetRequiredService<ILocalToolOfferProvider>();
        AllowedToolDto[] offeredTools =
        [
            .. resolved.AllowedTools,
            .. offerProvider.GetIntegrationOutputOffer()
                            .Select(tool => tool with
                            {
                                RequiresApproval = approvalPolicy.RequiresApproval(tool.Name, tool.Category, tool.RequiresApproval)
                            })
        ];

        var messageId = Guid.NewGuid();
        var package = services.GetRequiredService<ILocalChatRuntimePackageBuilder>()
                              .Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
                                  session.ConversationId,
                                  resolved.ResolvedSystemPrompt,
                                  conversationContext,
                                  effectiveModel,
                                  resolved.AgentDefinitionVersion,
                                  LocalChatLoopbackDefaults.ClientNodeId,
                                  offeredTools,
                                  RequestedCapabilities: [LocalChatLoopbackDefaults.RequestedCapability],
                                  Timeouts: new TimeoutSettings
                                  {
                                      InvocationTimeoutSeconds = nodeSettings.MaxMessageRequestTimeoutSeconds
                                  },
                                  ReasoningEffort: resolved.ReasoningEffort,
                                  SupportsThinking: supportsThinking,
                                  ReasoningBudgetEnforceable: capabilities.ReasoningBudgetEnforceable,
                                  Skills: resolved.Skills,
                                  IsUnattended: true));

        // 6. Subscribe BEFORE the lease. This is the ONE subscription lifetime for the whole run: it cannot miss a
        //    terminal report, and it closes in step 10's finally after the drain and after the terminal append, so no
        //    event raised during the drain is dropped.
        var dispatcher = services.GetRequiredService<IWorkerEventDispatcher>();
        var terminalState = new StrongBox<InvocationState?>(null);

        void OnInvocationStateChanged(object? sender, InvocationStateChangedEventArgs args)
        {
            if (args.State.InvocationId == package.InvocationId
                && args.State.Status is InvocationStatus.Completed or InvocationStatus.Failed or InvocationStatus.Cancelled)
            {
                terminalState.Value = args.State;
            }
        }

        // The stream mapper rides THIS subscription rather than opening a second one: two lifetimes could not both be
        // closed after the drain and before the terminal, which is the ordering the reader's completion rule needs.
        await using var mapper = new IntegrationStreamEventMapper(_buffer,
            context.Store,
            context.ExecutionId,
            session.Id,
            package.InvocationId,
            _options.MaxOutputBytes,
            TimeSpan.FromMilliseconds(services.GetRequiredService<IOptions<ChatStreamBudgetOptions>>().Value.EmitDebounceMs),
            _timeProvider,
            // The coordinator's own logger: the mapper rides this run's subscription and has no lifetime of its own.
            _logger);

        dispatcher.InvocationStateChanged += OnInvocationStateChanged;
        dispatcher.InvocationStateChanged += mapper.OnInvocationStateChanged;
        dispatcher.ToolCallLifecycleChanged += mapper.OnToolCallLifecycleChanged;
        try
        {
            await RunLeasedAsync(services,
                    context,
                    package,
                    session,
                    messageId,
                    effectiveModel,
                    terminalState,
                    dispatcher,
                    mapper,
                    persistence,
                    runToken,
                    cancelToken,
                    stoppingToken)
                .ConfigureAwait(false);
        }
        finally
        {
            dispatcher.ToolCallLifecycleChanged -= mapper.OnToolCallLifecycleChanged;
            dispatcher.InvocationStateChanged -= mapper.OnInvocationStateChanged;
            dispatcher.InvocationStateChanged -= OnInvocationStateChanged;
        }
    }

    private async Task RunLeasedAsync(IServiceProvider services,
        ExecutionRunContext context,
        RuntimePackage package,
        IntegrationSessionSnapshot session,
        Guid messageId,
        string effectiveModel,
        StrongBox<InvocationState?> terminalState,
        IWorkerEventDispatcher dispatcher,
        IntegrationStreamEventMapper mapper,
        INodeChatPersistenceService persistence,
        CancellationToken runToken,
        CancellationToken cancelToken,
        CancellationToken stoppingToken)
    {
        var store = context.Store;
        var executionId = context.ExecutionId;

        // 7a0. The queue-age pre-check is the cheap exit for a row that is already dead. It is NOT the bound: a row
        //      that passes here at 119 s could otherwise wait behind a cold model load indefinitely.
        var deadlineUtc = context.ReceivedAtUtc + (_options.MaxQueueAgeSeconds * 1000L);
        var remaining = deadlineUtc - NowUnixMilliseconds();
        if (remaining <= 0)
        {
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.QueueTimeout, "The execution waited longer than this node's maximum queue age.").ConfigureAwait(false);
            return;
        }

        // R5-2: the wait itself runs under the remaining budget. The dispatcher's first act is to await its
        // SemaphoreSlim on the token it was handed, so the expiry surfaces as an OperationCanceledException with no
        // lease held. This token goes to the lease request and NOWHERE else — the run must not inherit a token that
        // expires mid-generation.
        using var queueDeadline = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        queueDeadline.CancelAfter(TimeSpan.FromMilliseconds(remaining));

        var leaseTask = dispatcher.ReportInvocationAssignedAsync(package, queueDeadline.Token);

        try
        {
            // 7a. A free slot completes the task synchronously, so an incomplete task is an exact, allocation-free
            //     "this one had to wait". Accepted -> Running directly is legal; Queued exists only for a real wait,
            //     and this is its only producer.
            if (!leaseTask.IsCompleted
                && await store.UpdateStatusAsync(new IntegrationExecutionStatusUpdate(executionId, context.Version, AcceptedOnly, IntegrationExecutionStatus.Queued), runToken)
                              .ConfigureAwait(false))
            {
                // A false means a concurrent cancel already CASed the row on the same version, so the row is terminal
                // and no execution.queued may follow it.
                context.Version++;
                var queued = _buffer.Append(executionId, session.Id, IntegrationStreamEventTypes.ExecutionQueued, contentType: null, payload: null);
                await store.AppendEventAsync(new IntegrationEventAppend(Guid.NewGuid(), executionId, queued.Sequence, queued.Type, DetailJson: null, queued.OccurredAtUtc), runToken)
                           .ConfigureAwait(false);
            }
        }
        catch
        {
            // The lease request is still in flight and this frame is about to unwind past the only await that would
            // have taken ownership of it. Disposing `queueDeadline` does NOT cancel a pending SemaphoreSlim wait, so
            // the permit would be granted to nobody and held forever — and that semaphore is the node's ONE invocation
            // slot, shared with chat, regeneration, the scheduler and the benchmark executors.
            //
            // Cancelling first is what keeps this bounded: the wait unwinds at once with no permit held, and in the
            // race where it was granted a moment earlier the lease comes back here and is disposed. Never a detached
            // task — the settle has to happen before the fault handler runs, or the row terminalizes while the slot is
            // still held.
            await queueDeadline.CancelAsync().ConfigureAwait(false);
            try
            {
                var orphan = await leaseTask.ConfigureAwait(false);
                await orphan.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception reclaimFailure)
            {
                // The wait unwound with no permit, which is the ordinary outcome of the cancel above.
                _logger.LogDebug(reclaimFailure, "The orphaned invocation lease for integration execution {ExecutionId} held no permit.", executionId);
            }

            throw;
        }

        IAsyncDisposable lease;
        try
        {
            lease = await leaseTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 7b. Three causes reach here with no lease held, and they must not be conflated.
            await ReloadVersionAsync(context).ConfigureAwait(false);
            if (cancelToken.IsCancellationRequested)
            {
                await TerminalizeAsync(context, BeforeRunStatuses, IntegrationExecutionStatus.Cancelled, failureCategory: null, failureSummary: null).ConfigureAwait(false);
            }
            else if (stoppingToken.IsCancellationRequested)
            {
                await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.Shutdown, "The node stopped while the execution was waiting for the invocation lease.").ConfigureAwait(false);
            }
            else
            {
                await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.QueueTimeout, "The execution waited longer than this node's maximum queue age.").ConfigureAwait(false);
            }

            return;
        }

        IDisposable? reservation = null;
        try
        {
            // 7b1. A lease acquired at or past the deadline is still a stale run: the caller has been told, or has
            //      given up, and the node's only invocation slot is about to be spent on a result nobody reads. Checked
            //      before capacity, before the Running CAS, before any side effect at all.
            if (NowUnixMilliseconds() >= deadlineUtc)
            {
                await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.QueueTimeout, "The execution waited longer than this node's maximum queue age.").ConfigureAwait(false);
                return;
            }

            // 7b2. Capacity, with the lease already held. The scheduler decides capacity first and holds the footprint
            //      reservation across the whole lease wait; reversing the pair is deliberate, so a queued integration
            //      run cannot fail a concurrent interactive turn's capacity decision while it waits. Disposed in
            //      reverse acquisition order below.
            var decision = await services.GetRequiredService<ICapacityService>().DecideAsync(effectiveModel, ModelRole.Chat, runToken).ConfigureAwait(false);
            if (decision.Verdict == CapacityVerdict.RejectInsufficient)
            {
                await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.CapacityRejected, "The node could not reserve capacity for the trigger's model.").ConfigureAwait(false);
                return;
            }

            reservation = decision.Reservation;

            // 7c. A cancel that arrived while the lease was being awaited lands here.
            var current = await store.GetByIdAsync(executionId, runToken).ConfigureAwait(false);
            if (current is null)
            {
                return;
            }

            context.Version = current.Version;
            if (current.Status == IntegrationExecutionStatus.Cancelled || !NonTerminalStatuses.Contains(current.Status))
            {
                return;
            }

            if (current.StopRequestedAtUtc is not null)
            {
                await TerminalizeAsync(context, BeforeRunStatuses, IntegrationExecutionStatus.Cancelled, failureCategory: null, failureSummary: null).ConfigureAwait(false);
                return;
            }

            // 7d. The invocation id is stamped in this same update: the column is the audit row's correlation and
            //     nothing else ever writes it.
            var startedAtUtc = NowUnixMilliseconds();
            if (!await store.UpdateStatusAsync(new IntegrationExecutionStatusUpdate(executionId,
                                     context.Version,
                                     BeforeRunStatuses,
                                     IntegrationExecutionStatus.Running,
                                     startedAtUtc,
                                     EndedAtUtc: null,
                                     package.InvocationId),
                                 runToken)
                             .ConfigureAwait(false))
            {
                var reloaded = await store.GetByIdAsync(executionId, runToken).ConfigureAwait(false);
                if (reloaded is null || !NonTerminalStatuses.Contains(reloaded.Status))
                {
                    return;
                }

                context.Version = reloaded.Version;
                await TerminalizeFromFaultAsync(context,
                        IntegrationFailureCategories.InternalFailure,
                        $"The execution could not be moved to Running from {reloaded.Status}.")
                    .ConfigureAwait(false);
                return;
            }

            context.Version++;
            context.InvocationId = package.InvocationId;

            // 7e. execution.started, then the assistant placeholder. Terminalization correlates on
            //     (ConversationId, MessageId, RequestId) against an EXISTING placeholder row, so creating it after the
            //     run would leave the assistant turn unpersisted.
            var started = _buffer.Append(executionId, session.Id, IntegrationStreamEventTypes.ExecutionStarted, contentType: null, payload: null);
            await store.AppendEventAsync(new IntegrationEventAppend(Guid.NewGuid(), executionId, started.Sequence, started.Type, DetailJson: null, started.OccurredAtUtc), runToken)
                       .ConfigureAwait(false);

            var correlation = new NodeChatMessageCorrelation(session.ConversationId, messageId, executionId);
            _ = await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(session.ConversationId,
                                         messageId,
                                         executionId,
                                         startedAtUtc,
                                         effectiveModel,
                                         MetadataJson: null,
                                         NodeChatOriginValues.Local,
                                         session.AgentDefinitionId),
                                     runToken)
                                 .ConfigureAwait(false);

            // 8. Run.
            string? failureCategory = null;
            string? failureSummary = null;
            try
            {
                var runner = services.GetRequiredService<IInvocationRunner>();

                // B5 asks for BOTH. The linked run token stops the generation, but only Cancel() cancels the run's
                // pending tool calls and attributes the turn to CancellationOrigin.User rather than to a bare abort.
                // Registered for the CURRENT run only, and unregistered with it.
                await using var cancelBridge = cancelToken.Register(() => runner.Cancel(package.InvocationId));
                using var executionContext = InvocationExecutionContext.CreatePlain(package, Guid.Empty);
                await runner.RunAsync(executionContext, runToken).ConfigureAwait(false);
            }
            catch (ApprovalUnavailableException approvalUnavailable)
            {
                // Defended even though the runner classifies and reports this one itself rather than letting it out:
                // if a future runner rethrows, the category must not silently collapse into internal-failure.
                failureCategory = IntegrationFailureCategories.ApprovalRequired;
                failureSummary = approvalUnavailable.Message;
            }

            await FinishAsync(services, context, correlation, terminalState.Value, effectiveModel, mapper, failureCategory, failureSummary).ConfigureAwait(false);
        }
        finally
        {
            // Reverse acquisition order: a leaked reservation wrongly rejects later spawns, and disposing a null one
            // (QueueSameModel) is a no-op. The inner try is not decoration: a throw from the reservation would
            // otherwise skip the lease and starve every later run on the node.
            try
            {
                reservation?.Dispose();
            }
            finally
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task FinishAsync(IServiceProvider services,
        ExecutionRunContext context,
        NodeChatMessageCorrelation correlation,
        InvocationState? state,
        string effectiveModel,
        IntegrationStreamEventMapper mapper,
        string? failureCategory,
        string? failureSummary)
    {
        var status = IntegrationExecutionStatus.Failed;

        // 9. The assistant turn, from the terminal state. Parts stays null: nothing persists tool events for a plain
        //    context, so leaving them untouched is the honest answer rather than an empty claim.
        if (state is null)
        {
            // The runner returned without reporting. Do not dereference; the row's reason names the case.
            failureCategory ??= IntegrationFailureCategories.InternalFailure;
            failureSummary ??= "The invocation returned without reporting a terminal state.";
            _ = await services.GetRequiredService<INodeChatPersistenceService>()
                              .TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation, NodeChatMessageStatusValues.Failed, NowUnixMilliseconds()),
                                  CancellationToken.None)
                              .ConfigureAwait(false);
        }
        else
        {
            var terminalStatus = state.Status switch
            {
                InvocationStatus.Completed => NodeChatMessageStatusValues.Completed,
                InvocationStatus.Cancelled => NodeChatMessageStatusValues.Cancelled,
                _ => NodeChatMessageStatusValues.Failed
            };

            status = state.Status switch
            {
                InvocationStatus.Completed => IntegrationExecutionStatus.Completed,
                InvocationStatus.Cancelled => IntegrationExecutionStatus.Cancelled,
                _ => IntegrationExecutionStatus.Failed
            };

            if (status == IntegrationExecutionStatus.Failed)
            {
                // The runner classifies an unattended approval refusal as AgentRuntime and surfaces its own fixed-shape
                // reason verbatim, so the prefix is what distinguishes "this agent needs a capability it cannot have
                // unattended" from "something broke" — the whole reason the tools are not stripped.
                if (failureCategory is null
                    && state.Error is { } error
                    && error.StartsWith(ApprovalUnavailableException.UnattendedReasonPrefix, StringComparison.Ordinal))
                {
                    failureCategory = IntegrationFailureCategories.ApprovalRequired;
                    failureSummary = error;
                }

                failureCategory ??= IntegrationFailureCategories.InternalFailure;
                // The runner's own category enum name, never provider text.
                failureSummary ??= state.FailureCategory is { } runnerCategory
                    ? $"The invocation failed ({runnerCategory})."
                    : "The invocation failed.";
            }

            var durationMs = state.GenerationDurationMs
                             ?? (state.CompletedAt is { } completedAt ? Math.Max(val1: 0L, (long)(completedAt - state.StartedAt).TotalMilliseconds) : 0L);
            var provider = await services.GetRequiredService<IUsageProviderResolver>()
                                         .ResolveAsync(state.ModelUsed ?? effectiveModel, CancellationToken.None)
                                         .ConfigureAwait(false);

            // The envelope is not optional: SummarizeTokenUsageAsync reads only kind-1 rows, so omitting it would make
            // an external surface the one path that can silently burn tokens invisibly. The kind-3 audit row does not
            // fill that gap — it carries a terminal status and a latency, not the token columns.
            var envelope = new AgentRunEnvelopeMetadata(state.InvocationId,
                durationMs,
                state.FailureCategory?.ToString(),
                state.StreamedChunkCount,
                state.StreamedThinkingChunkCount,
                Activity.Current?.TraceId.ToString(),
                state.StartedAt == default ? null : state.StartedAt.ToUnixTimeMilliseconds(),
                provider);

            // Carried to the terminal event: `execution.completed` is `{tokens?, durationMs}`, and this is the one
            // place both numbers exist.
            context.RunDurationMs = durationMs;
            context.TotalTokens = state.TotalTokens;

            _ = await services.GetRequiredService<INodeChatPersistenceService>()
                              .TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                                      terminalStatus,
                                      NowUnixMilliseconds(),
                                      state.StreamedContent,
                                      Reasoning: null,
                                      // A cancelled turn persists NO error text: a cancel is an outcome, not a failure.
                                      terminalStatus == NodeChatMessageStatusValues.Cancelled ? null : state.Error,
                                      state.ModelUsed ?? effectiveModel,
                                      state.InputTokens,
                                      state.OutputTokens,
                                      state.TotalTokens,
                                      state.ReasoningTokens,
                                      Parts: null,
                                      state.GenerationDurationMs,
                                      envelope),
                                  CancellationToken.None)
                              .ConfigureAwait(false);
        }

        // 9b. THE DRAIN SEAM. The mapper persists tool.* rows off a channel, and without an awaited drain here one of
        //     them can land AFTER the terminal event — which a reader that stops on the terminal would never see. The
        //     drain latches the handlers shut too, so the terminal appended below is provably the highest sequence for
        //     the execution. CancellationToken.None: past the run, these rows carry sequences the ring has already
        //     published, so abandoning the write would leave a visible event with no durable row behind it.
        try
        {
            await mapper.DrainAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // An incomplete transcript is not a completed run, whatever the model did: the terminal is still written,
            // but it says internal-failure rather than the run's own status.
            _logger.LogError(exception, "Integration execution {ExecutionId} could not persist its tool events.", context.ExecutionId);
            status = IntegrationExecutionStatus.Failed;
            failureCategory = IntegrationFailureCategories.InternalFailure;
            failureSummary = "The execution's tool events could not be persisted.";
        }

        // 10. One transaction: the status CAS, the terminal event at the reserved sequence, and both watermarks. Never
        //     Append for a terminal event (that publishes before the row exists), and never a status CAS followed by a
        //     separate event insert (a crash between them leaves a terminal row whose terminal event never arrives, and
        //     startup recovery scans only NON-terminal rows, so the inconsistency would be permanent).
        _ = await TerminalizeAsync(context, RunningOnly, status, failureCategory, failureSummary).ConfigureAwait(false);
    }

    /// <summary>
    ///     Replays the session's committed <c>external.output</c> payloads back to the model as DATA, so a continued run
    ///     can tell a result it already delivered from prose it merely wrote — the property a caller-managed session
    ///     cannot otherwise have while tool parts are not persisted.
    ///     <para>
    ///         Two reads, no new store method: the session's most recent executions newest-first, then each one's
    ///         persisted events. The CURRENT execution is skipped (it has committed nothing yet) and so is any row whose
    ///         <c>OutputCount</c> is zero, so a session of pure-prose turns costs one indexed query and no more.
    ///     </para>
    ///     <para>
    ///         Only COMMITTED rows are read, which is what makes the replay match what the caller actually received: a
    ///         reserved-but-abandoned sequence never became a row, so it leaves no trace here.
    ///     </para>
    /// </summary>
    private async Task<ConversationMessageDto?> BuildPriorOutputsAsync(IServiceProvider services,
        IntegrationSessionSnapshot session,
        Guid currentExecutionId,
        CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<IIntegrationExecutionStore>();
        var executions = await store.ListAsync(new IntegrationExecutionFilter(TriggerId: null,
                    session.Id,
                    Status: null,
                    IntegrationPriorOutputsComposer.MaxPayloads,
                    Offset: 0),
                cancellationToken)
            .ConfigureAwait(false);

        var envelopes = new List<string>(IntegrationPriorOutputsComposer.MaxPayloads);
        foreach (var execution in executions)
        {
            if (execution.Id == currentExecutionId || execution.OutputCount == 0)
            {
                continue;
            }

            // ponytail: a single page rather than a paging loop — an execution's persisted rows are bounded by the
            // 40-iteration tool cap (a handful of phase events, at most 80 tool.* and at most 40 external.output). If
            // MaximumToolIterationsPerRequest is ever raised, raise this with it.
            var events = await store.ListEventsAsync(execution.Id, sinceSequence: 0, limit: 200, cancellationToken).ConfigureAwait(false);
            for (var index = events.Count - 1; index >= 0; index--)
            {
                var persisted = events[index];
                if (string.Equals(persisted.EventType, IntegrationStreamEventTypes.ExternalOutput, StringComparison.Ordinal)
                    && persisted.DetailJson is { } detail)
                {
                    // Already the composed {"contentType": …, "payload": …} envelope the tool wrote, and DECRYPTED by
                    // the store. Emitted verbatim: nothing is re-parsed.
                    envelopes.Add(detail);
                }
            }

            if (envelopes.Count >= IntegrationPriorOutputsComposer.MaxPayloads)
            {
                break;
            }
        }

        var content = IntegrationPriorOutputsComposer.Compose(envelopes,
            _options.PriorOutputsContextBytes,
            services.GetRequiredService<IUntrustedContentFenceSeedProvider>().DeriveSeed(session.ConversationId));
        return content is null
            ? null
            : new ConversationMessageDto
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.User,
                Content = content,
                SortOrder = 0
            };
    }

    /// <summary>
    ///     Closes the session of a terminalized <c>PerInvocation</c> execution. A per-invocation session exists for one
    ///     run, so leaving it <c>Active</c> would show an operator a session nothing will ever join. A
    ///     <c>CallerManaged</c> session is closed only by the operator's delete, which is the whole point of the policy.
    ///     <para>
    ///         Best effort and never fatal: the execution is already terminal and its caller has already been answered,
    ///         so a failure here is a log line rather than a reason to reopen a committed terminal.
    ///     </para>
    /// </summary>
    private async Task ClosePerInvocationSessionAsync(IServiceProvider services, IntegrationExecutionSnapshot execution)
    {
        try
        {
            var row = await services.GetRequiredService<IIntegrationExecutionStore>().GetByIdAsync(execution.Id, CancellationToken.None).ConfigureAwait(false);
            if (row is null || NonTerminalStatuses.Contains(row.Status))
            {
                return;
            }

            var trigger = await services.GetRequiredService<IIntegrationTriggerStore>().GetByIdAsync(execution.TriggerId, CancellationToken.None).ConfigureAwait(false);
            if (trigger is null || trigger.SessionPolicy != IntegrationSessionPolicy.PerInvocation)
            {
                return;
            }

            _ = await services.GetRequiredService<IntegrationSessionService>().CloseAsync(execution.SessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "The per-invocation session {SessionId} of integration execution {ExecutionId} could not be closed.",
                execution.SessionId,
                execution.Id);
        }
    }

    /// <summary>
    ///     Terminalizes a row that never reached <c>Running</c>. A legal <c>Accepted|Queued -> Failed</c> edge for
    ///     exactly the pre-run rejections; an ordinary failure does NOT have to pass through <c>Running</c> first.
    /// </summary>
    private Task TerminalizeBeforeRunAsync(ExecutionRunContext context, string failureCategory, string failureSummary) =>
        TerminalizeAsync(context, BeforeRunStatuses, IntegrationExecutionStatus.Failed, failureCategory, failureSummary);

    /// <summary>
    ///     The safety net for a throw anywhere in the pipeline: re-read the row so the CAS carries a version that is
    ///     actually current, and terminalize whatever non-terminal status it is in.
    /// </summary>
    private async Task TerminalizeFromFaultAsync(ExecutionRunContext context, string failureCategory, string failureSummary)
    {
        try
        {
            var row = await context.Store.GetByIdAsync(context.ExecutionId, CancellationToken.None).ConfigureAwait(false);
            if (row is null || !NonTerminalStatuses.Contains(row.Status))
            {
                return;
            }

            if (!_buffer.IsTracked(context.ExecutionId) && !_buffer.TryCreate(context.ExecutionId, row.LastSequence))
            {
                _logger.LogWarning("The event buffer refused an entry for integration execution {ExecutionId}; it stays non-terminal for the next restart.", context.ExecutionId);
                return;
            }

            context.Version = row.Version;
            _ = await TerminalizeAsync(context, NonTerminalStatuses, IntegrationExecutionStatus.Failed, failureCategory, failureSummary).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Integration execution {ExecutionId} could not be terminalized after a fault.", context.ExecutionId);
        }
    }

    /// <summary>
    ///     The ONE terminal shape: reserve a sequence privately, commit the status and the event together, and only
    ///     then publish. A lost CAS or a throw abandons the reservation — an unresolved one is not a hole readers
    ///     tolerate, it is a stall that parks every reader on this execution until the entry is evicted.
    ///     <para>
    ///         Whoever's CAS returns <see langword="true" /> owns the terminal artefacts: the published event and the
    ///         one kind-3 audit row. A caller that finds the row already terminal publishes nothing and audits nothing,
    ///         so a queued cancel cannot produce two cancelled events and two audit rows.
    ///     </para>
    ///     <para>
    ///         The single retry exists because the cancel path stamps its durable stop marker through a NON-terminal
    ///         status update, which bumps the row's version without terminalizing it. Without the retry a coordinator
    ///         holding the pre-marker version would lose its CAS and leave the row stuck.
    ///     </para>
    /// </summary>
    private async Task<bool> TerminalizeAsync(ExecutionRunContext context,
        IReadOnlySet<IntegrationExecutionStatus> expectedStatuses,
        IntegrationExecutionStatus status,
        string? failureCategory,
        string? failureSummary)
    {
        if (await TryTerminalizeOnceAsync(context, expectedStatuses, status, failureCategory, failureSummary).ConfigureAwait(false))
        {
            return true;
        }

        var fresh = await context.Store.GetByIdAsync(context.ExecutionId, CancellationToken.None).ConfigureAwait(false);
        if (fresh is null || !expectedStatuses.Contains(fresh.Status) || fresh.Version == context.Version)
        {
            return false;
        }

        context.Version = fresh.Version;
        return await TryTerminalizeOnceAsync(context, expectedStatuses, status, failureCategory, failureSummary).ConfigureAwait(false);
    }

    private async Task<bool> TryTerminalizeOnceAsync(ExecutionRunContext context,
        IReadOnlySet<IntegrationExecutionStatus> expectedStatuses,
        IntegrationExecutionStatus status,
        string? failureCategory,
        string? failureSummary)
    {
        var eventType = status switch
        {
            IntegrationExecutionStatus.Completed => IntegrationStreamEventTypes.ExecutionCompleted,
            IntegrationExecutionStatus.Cancelled => IntegrationStreamEventTypes.ExecutionCancelled,
            _ => IntegrationStreamEventTypes.ExecutionFailed
        };

        var endedAtUtc = NowUnixMilliseconds();

        // ONE payload for both writes below. A terminal frame with a null payload tells an integrator nothing about
        // why the run ended, and the row would then carry a reason the stream never gave.
        var payload = status switch
        {
            // The run's own duration when the invocation reported one; otherwise the wall time since the request was
            // admitted, which includes the queue wait but is never absent.
            IntegrationExecutionStatus.Completed => IntegrationTerminalPayload.Completion(context.TotalTokens,
                context.RunDurationMs ?? Math.Max(val1: 0L, endedAtUtc - context.ReceivedAtUtc)),
            // `execution.cancelled` carries no payload by contract: a cancel is an outcome, not a failure.
            IntegrationExecutionStatus.Cancelled => (JsonElement?)null,
            _ => IntegrationTerminalPayload.Failure(failureCategory, failureSummary)
        };

        var sequence = _buffer.Reserve(context.ExecutionId);
        var published = false;
        try
        {
            // Terminal writes never carry the run's cancellation token: a shutdown must still be able to close the row.
            var won = await context.Store.TryTerminalizeAsync(new IntegrationTerminalizeCommand(context.ExecutionId,
                                             context.Version,
                                             expectedStatuses,
                                             status,
                                             sequence,
                                             eventType,
                                             endedAtUtc,
                                             failureCategory,
                                             failureSummary,
                                             payload?.GetRawText()),
                                         CancellationToken.None)
                                     .ConfigureAwait(false);
            if (!won)
            {
                return false;
            }

            _buffer.Publish(new IntegrationStreamEvent(eventType,
                sequence,
                context.ExecutionId,
                context.SessionId,
                endedAtUtc,
                ContentType: null,
                payload));
            published = true;
            context.Version++;

            await WriteAuditAsync(context, status, endedAtUtc).ConfigureAwait(false);
            return true;
        }
        finally
        {
            if (!published)
            {
                _buffer.Abandon(context.ExecutionId, sequence);
            }
        }
    }

    /// <summary>
    ///     The ONE kind-3 audit row per execution, written by whoever won the terminal CAS. Content-free by contract:
    ///     ids, a trigger name, a credential prefix and a terminal status.
    /// </summary>
    private async Task WriteAuditAsync(ExecutionRunContext context, IntegrationExecutionStatus status, long endedAtUtc)
    {
        try
        {
            await context.AuditLog.AddIntegrationInvocationAsync(new IntegrationInvocationAuditInput(context.InvocationId,
                    context.RequestId,
                    context.TriggerName,
                    context.KeyPrefix,
                    context.TargetAgentDefinitionId,
                    status switch
                    {
                        IntegrationExecutionStatus.Completed => NodeChatMessageStatusValues.Completed,
                        IntegrationExecutionStatus.Cancelled => NodeChatMessageStatusValues.Cancelled,
                        _ => NodeChatMessageStatusValues.Failed
                    },
                    Activity.Current?.TraceId.ToString(),
                    Math.Max(val1: 0L, endedAtUtc - context.ReceivedAtUtc)),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The row is already terminal and the event is already published. Losing the audit row is worth a log line,
            // not a reversal of a committed terminal.
            _logger.LogError(exception, "The kind-3 audit row for integration execution {ExecutionId} could not be written.", context.ExecutionId);
        }
    }

    private static async Task ReloadVersionAsync(ExecutionRunContext context)
    {
        var row = await context.Store.GetByIdAsync(context.ExecutionId, CancellationToken.None).ConfigureAwait(false);
        if (row is not null)
        {
            context.Version = row.Version;
        }
    }

    private long NowUnixMilliseconds() =>
        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>
    ///     Everything a terminal write needs, in one place, so the fixed terminal shape is a call rather than eleven
    ///     arguments repeated at every site.
    /// </summary>
    private sealed class ExecutionRunContext
    {
        public ExecutionRunContext(IIntegrationExecutionStore store, IAgentExecutionLogStore auditLog, IntegrationExecutionSnapshot execution)
        {
            Store = store;
            AuditLog = auditLog;
            ExecutionId = execution.Id;
            SessionId = execution.SessionId;
            RequestId = execution.RequestId;
            KeyPrefix = execution.KeyPrefix;
            ReceivedAtUtc = execution.ReceivedAtUtc;
            InvocationId = execution.InvocationId;
            Version = execution.Version;

            // Until the trigger is loaded the row's own id IS the trigger's most specific available name. A deleted
            // trigger never gets a better one, and a fabricated label would be worse than a resolvable id.
            TriggerName = execution.TriggerId.ToString("D");
        }

        public IIntegrationExecutionStore Store { get; }

        public IAgentExecutionLogStore AuditLog { get; }

        public Guid ExecutionId { get; }

        public Guid SessionId { get; }

        public Guid RequestId { get; }

        public string KeyPrefix { get; }

        public long ReceivedAtUtc { get; }

        /// <summary>The run's own duration and token total, read off the terminal invocation state for the terminal event.</summary>
        public long? RunDurationMs { get; set; }

        public int? TotalTokens { get; set; }

        public string TriggerName { get; private set; }

        public Guid TargetAgentDefinitionId { get; private set; }

        public Guid InvocationId { get; set; }

        public long Version { get; set; }

        public void Describe(string triggerName, Guid targetAgentDefinitionId)
        {
            TriggerName = triggerName;
            TargetAgentDefinitionId = targetAgentDefinitionId;
        }
    }
}
