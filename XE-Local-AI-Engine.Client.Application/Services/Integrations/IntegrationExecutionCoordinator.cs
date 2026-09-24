namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
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
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     The single consumer of the accept path's queue, the only component that runs an integration execution, and the
///     only producer of an execution's terminal event.
/// </summary>
/// <remarks>
///     It drives the SAME seam the scheduler's <c>run-agent</c> template uses — <see cref="IAgentDefinitionResolver" />
///     + <see cref="ILocalChatRuntimePackageBuilder" /> + <see cref="InvocationExecutionContext.CreatePlain" /> +
///     <see cref="IInvocationRunner" /> — with <c>IsUnattended: true</c>, serialised behind the node's single
///     invocation lease, and introduces no second runtime path. It diverges from the scheduler in three ways, each
///     with its reason: see docs/adr/0008-external-integrations.md ("Invariants the coordinator enforces").
/// </remarks>
internal sealed partial class IntegrationExecutionCoordinator : BackgroundService
{
    /// <summary>
    ///     How many EVENTS <see cref="HighestPersistedSequenceAsync" /> pulls per page. Events are unbounded — one
    ///     execution can write as many as it likes — so that read genuinely has to page. The row sweep does not: see
    ///     <see cref="ReconcileInterruptedAsync" />.
    /// </summary>
    private const int RecoveryEventPageSize = 200;

    /// <summary>
    ///     How many times a dispatch fault, or the whole startup sweep, is retried: these retries exist for a transient
    ///     store failure, and a fault that survives three attempts is not one.
    /// </summary>
    private const int MaxRecoveryAttempts = 3;

    /// <summary>The pause between those attempts. Short, because an admitted execution's caller is waiting on it.</summary>
    private static readonly TimeSpan RecoveryRetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>How many times a terminal transition may reload the row and try again.</summary>
    /// <remarks>
    ///     Bounded rather than open-ended: each loss costs a reserved sequence, and a writer that drifts the version
    ///     three times running is a caller hammering cancel, not a race that one more read would settle.
    /// </remarks>
    private const int MaxTerminalAttempts = 4;

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
    ///     Kestrel's included — has started.
    /// </summary>
    /// <remarks>
    ///     The startup sweep compares against it instead of assuming a registration order it does not control: the
    ///     listener may already be accepting requests while the sweep pages, and a row admitted in that window holds a
    ///     202 its caller has been given.
    /// </remarks>
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
    ///     Reconciles every row this process cannot resume before the consumer loop starts, so the loop never reads an
    ///     id the sweep has not visited yet.
    /// </summary>
    /// <remarks>
    ///     There is exactly ONE sweep: admission commits before the owned conversation is created, so no orphan
    ///     conversation can exist and there is nothing else to reclaim.
    /// </remarks>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // A bounded retry covers a transient store failure; past it the host refuses to start, as McpAgentRunRecoveryService does. A swallowed sweep failure
        // leaves every interrupted row non-terminal and still counted against its principal's admission cap: a node serving 503s with no in-flight work.
        for (var attempt = 1; attempt <= MaxRecoveryAttempts; attempt++)
        {
            try
            {
                await ReconcileInterruptedAsync(cancellationToken);
                break;
            }
            catch (Exception exception) when (attempt < MaxRecoveryAttempts && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception,
                    "Integration execution startup recovery failed on attempt {Attempt} of {MaxAttempts}; retrying.",
                    attempt,
                    MaxRecoveryAttempts);
                await Task.Delay(RecoveryRetryDelay, _timeProvider, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogCritical(exception, "Integration execution startup recovery failed; the node cannot admit executions safely.");
                throw;
            }
        }

        await base.StartAsync(cancellationToken);
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

            var execution = await store.GetByIdAsync(executionId, CancellationToken.None);
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

            var context = new ExecutionRunContext(store, execution);

            try
            {
                await ExecuteOneAsync(scope.ServiceProvider, context, execution, runCancellation.Token, cancelToken, stoppingToken);
            }
            catch (Exception exception)
            {
                // Every stage is wrapped so a throw still terminalizes: a row stuck Running holds its principal's admission slot forever. A cancel is
                // classified BEFORE shutdown — the run's own token is what the cancel signals, and a cancelled caller must not be told internal-failure.
                if (exception is OperationCanceledException && cancelToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Integration execution {ExecutionId} was cancelled while in flight.", executionId);
                    await TerminalizeFromFaultAsync(context, IntegrationExecutionStatus.Cancelled, failureCategory: null, failureSummary: null);
                }
                else
                {
                    var shutdown = stoppingToken.IsCancellationRequested && exception is OperationCanceledException;
                    _logger.LogError(exception, "Integration execution {ExecutionId} faulted; terminalizing it.", executionId);
                    await TerminalizeFromFaultAsync(context,
                        IntegrationExecutionStatus.Failed,
                        shutdown ? IntegrationFailureCategories.Shutdown : IntegrationFailureCategories.InternalFailure,
                        shutdown ? "The node stopped while the execution was in flight." : "The execution failed unexpectedly.");
                }
            }

            // After every terminal path rather than inside one of them: an execution ends at a dozen points, the fault handler above included, and a
            // per-invocation session left Active by any of them stays Active forever.
            await ClosePerInvocationSessionAsync(scope.ServiceProvider, execution);
        }
        finally
        {
            _cancellations.Remove(executionId);
        }
    }

    /// <summary>
    ///     ONE channel reader, but processing is NOT serialised on it: each id is dispatched, and the node's invocation
    ///     lease — a <see cref="SemaphoreSlim" /> granting in wait order — is what serialises the runs.
    /// </summary>
    /// <remarks>
    ///     Awaiting <see cref="ProcessOneAsync" /> here would hold the next id in the channel for the whole of the
    ///     current run, and every deadline control plus the sole writer of <c>Queued</c> lives inside that method, so
    ///     R5-2's queue-age bound would measure the run ahead instead of the wait. The live-task count is bounded by
    ///     the admission cap, because each task holds a non-terminal row against it. See
    ///     docs/adr/0008-external-integrations.md ("Invariants the coordinator enforces").
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Touched only by this loop, so no lock: pruning on every dispatch keeps it at the live-task count.
        var inFlight = new List<Task>();
        try
        {
            // Queued ids left behind when the token trips are simply dropped: their rows are still Accepted, and the
            // next StartAsync sweep flips them to Failed / restart.
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
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
        await Task.WhenAll(inFlight);
    }

    /// <summary>
    ///     Runs one dispatched execution to completion, absorbing anything that escapes
    ///     <see cref="ProcessOneAsync" />'s own fault handling.
    /// </summary>
    /// <remarks>
    ///     A fault outside that handler — a store read that throws before it is in scope — must not take the reader
    ///     loop, and with it every later execution on this node, down with it.
    /// </remarks>
    private async Task RunDispatchedAsync(Guid executionId, CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= MaxRecoveryAttempts; attempt++)
        {
            try
            {
                await ProcessOneAsync(executionId, stoppingToken);
                return;
            }
            catch (Exception exception)
            {
                // The id is already out of the channel, so nothing else ever picks this execution up: leaving the row for the next restart sweep would cost
                // its principal an admission slot until the process is restarted.
                _logger.LogError(exception,
                    "Integration execution {ExecutionId} faulted outside its own handler on attempt {Attempt} of {MaxAttempts}.",
                    executionId,
                    attempt,
                    MaxRecoveryAttempts);

                if (attempt >= MaxRecoveryAttempts || stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await Task.Delay(RecoveryRetryDelay, _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        await TerminalizeStrandedAsync(executionId);
    }

    /// <summary>
    ///     Fails every row this process cannot resume: V1 does not resume an in-flight generation, so an interrupted
    ///     row becomes <c>Failed</c> / <c>restart</c>.
    /// </summary>
    /// <remarks>
    ///     Its terminal event is minted through the buffer at <c>LastSequence + 1</c>, so the sequence comes from the
    ///     one authority rather than from a hand-computed carve-out.
    /// </remarks>
    private async Task ReconcileInterruptedAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IIntegrationExecutionStore>();

        // ONE unpaged read of the whole non-terminal set, then ONE pass over that snapshot: the set both shrinks and grows under live admission, so no offset
        // over it is safe. Why that is affordable, and why a stale snapshot is harmless: ADR 0008 ("Invariants the coordinator enforces").
        var interrupted = await store.ListAsync(new IntegrationExecutionFilter
        {
            TriggerId = null,
            SessionId = null,
            Status = NonTerminalStatuses,
            Limit = int.MaxValue,
            Offset = 0
        }, cancellationToken);

        var recovered = 0;
        foreach (var row in interrupted)
        {
            if (row.ReceivedAtUtc >= _constructedAtUtc)
            {
                // Admitted after this coordinator existed, so it cannot be a leftover of the previous process: the
                // accept path enqueues every row it commits, and this one's caller is holding its 202.
                continue;
            }

            // R3-1: seed the ring from the highest sequence this execution can PROVE — its watermark, or a persisted event above it — so the sweep's terminal
            // event continues its OWN numbering; seeding below a lost-race event collides with an existing (execution_id, sequence) row on every restart.
            var seedSequence = await HighestPersistedSequenceAsync(store, row, cancellationToken);
            if (!_buffer.TryCreate(row.Id, seedSequence))
            {
                _logger.LogWarning("The event buffer refused a recovery entry for integration execution {ExecutionId}; it stays non-terminal for the next restart.", row.Id);
                continue;
            }

            var context = new ExecutionRunContext(store, row);
            if (await TerminalizeAsync(context,
                    NonTerminalStatuses,
                    IntegrationExecutionStatus.Failed,
                    IntegrationFailureCategories.Restart,
                    "The node restarted while the execution was in flight."))
            {
                recovered++;

                // The sweep is a DIFFERENT terminal path from the run's own and closes per-invocation sessions too, or a session interrupted by a restart
                // stays Active with no execution that could ever close it. The busy guard is bypassed by construction: the row is already terminal.
                await ClosePerInvocationSessionAsync(scope.ServiceProvider, row);
            }
        }

        if (recovered > 0)
        {
            _logger.LogWarning("Terminalized {Count} interrupted integration execution(s) during startup recovery.", recovered);
        }
    }

    /// <summary>The highest sequence this execution can prove: its row watermark, or a persisted event above it.</summary>
    /// <remarks>
    ///     Pages forward from the watermark rather than reading the whole feed, so the ordinary case — a watermark that
    ///     is already current — costs one empty page.
    /// </remarks>
    private static async Task<long> HighestPersistedSequenceAsync(IIntegrationExecutionStore store,
        IntegrationExecutionSnapshot row,
        CancellationToken cancellationToken)
    {
        var highest = row.LastSequence;
        while (true)
        {
            var page = await store.ListEventsAsync(row.Id, highest, RecoveryEventPageSize, cancellationToken);
            if (page.Count == 0)
            {
                return highest;
            }

            highest = page[^1].Sequence;
            if (page.Count < RecoveryEventPageSize)
            {
                return highest;
            }
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

        // 1. Everything the run needs. A missing conversation or seed is the ONE shape R4-1's forward-running failure leaves behind — the execution row
        //    commits before they are written — and it is failed, never repaired: the seed text is not recoverable, and a run against an empty seed is worse.
        var sessions = services.GetRequiredService<IIntegrationSessionStore>();
        var session = await sessions.GetByIdAsync(execution.SessionId, runToken);
        if (session is null)
        {
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.InternalFailure, "The execution's session row is missing.");
            return;
        }

        var trigger = await services.GetRequiredService<IIntegrationTriggerStore>().GetByIdAsync(execution.TriggerId, runToken);
        if (trigger is null || !trigger.Enabled)
        {
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.TriggerUnavailable, "The trigger was removed or disabled before the execution ran.");
            return;
        }

        context.Describe(trigger.Name, trigger.TargetAgentDefinitionId);

        var definition = await services.GetRequiredService<IAgentDefinitionStore>().GetByIdAsync(trigger.TargetAgentDefinitionId, runToken);
        if (definition is null)
        {
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.TriggerUnavailable, "The trigger's target agent no longer exists.");
            return;
        }

        // V1 runs a saved SINGLE agent: this package carries no OrchestrationSpec, so an orchestrator would report Completed having run no participant, no
        // routing and no handoff. Checked here as well as at save — a Kind can change. Refused, not emulated: ADR 0008 ("Invariants the coordinator enforces").
        if (definition.Kind != AgentDefinitionKind.Single)
        {
            await TerminalizeBeforeRunAsync(context,
                IntegrationFailureCategories.TriggerUnavailable,
                "The trigger's target agent is an orchestrator, which external integrations do not run.");
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
            await TerminalizeAsync(context, BeforeRunStatuses, IntegrationExecutionStatus.Cancelled, failureCategory: null, failureSummary: null);
            return;
        }

        // 3. The effective model, and the locality gate. A cloud model is rejected UP FRONT, before the lease and before the capacity decision, so
        //    unattended external work never egresses. The capacity decision itself is step 7b2, after the lease.
        var nodeSettings = await services.GetRequiredService<INodeSettingsStore>().LoadAsync(runToken);
        var localDefaultModel = await services.GetRequiredService<ILocalDefaultChatModelResolver>()
                                              .ResolveAsync(nodeSettings.DefaultModelName, runToken);
        var pinnedModel = string.IsNullOrWhiteSpace(definition.ModelProfile) ? null : definition.ModelProfile;
        var effectiveModel = pinnedModel ?? localDefaultModel;
        if (string.IsNullOrWhiteSpace(effectiveModel))
        {
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.TriggerUnavailable, "No local chat model is available to run the trigger's agent.");
            return;
        }

        var capabilities = await services.GetRequiredService<IModelCapabilityResolver>().ResolveAsync(effectiveModel, runToken);
        var (supportsThinking, supportsTools, effectiveModelIsCloud) = capabilities;
        if (effectiveModelIsCloud)
        {
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.CloudModelRejected, "The trigger's effective model is cloud-hosted, and unattended runs are node-local only.");
            return;
        }

        // 3b. The compaction bound, BEFORE the conversation read so the read sees the folded transcript; every no-op outcome is non-fatal by design. The keep
        //     window and the excerpt cap come from the CHAT options, and the projection counts replayed tool exchanges: ADR 0008 ("Invariants the coordinator enforces").
        var replaysToolHistory = trigger.SessionPolicy == IntegrationSessionPolicy.CallerManaged;
        var toolResultExcerptChars = services.GetRequiredService<IOptions<ConversationContextBudgetOptions>>().Value.HistoricalToolResultExcerptChars;
        await services.GetRequiredService<ConversationStepContextBound>()
                      .ApplyAsync(session.ConversationId,
                          _options.ContextBudgetTokens,
                          effectiveModel,
                          runToken,
                          services.GetRequiredService<IOptions<ConversationCompactionOptions>>().Value.RecentMessagesToKeepVerbatim,
                          replaysToolHistory,
                          toolResultExcerptChars);

        // 3c. The turn read. A caller-managed continuation takes the FULL read, not the capped turn read: its persisted tool parts live in the same
        //     metadata_json blob the capped read omits for non-user rows, so under a compacted session its replay would silently be empty.
        var conversation = replaysToolHistory
            ? await persistence.GetConversationAsync(session.ConversationId, runToken)
            : await persistence.GetConversationForTurnAsync(session.ConversationId, runToken);
        var seed = conversation?.Messages.FirstOrDefault(message => message.MessageId == executionId);
        if (conversation is null || seed is null)
        {
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.InternalFailure, "The execution's owned conversation or seed turn is missing.");
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
                                         runToken);
        if (resolved is null)
        {
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.TriggerUnavailable, "The trigger's target agent no longer exists.");
            return;
        }

        // The SECOND read of the definition's Kind, and the one this package is actually built from: the resolver re-reads the definition through its own
        // fresh query, so a switch to an orchestrator between the step-1 guard and this resolve is judged here rather than trusted from the earlier read.
        if (resolved.Kind != AgentDefinitionKind.Single)
        {
            await TerminalizeBeforeRunAsync(context,
                IntegrationFailureCategories.TriggerUnavailable,
                "The trigger's target agent is an orchestrator, which external integrations do not run.");
            return;
        }

        // 4c. The turn's context, from the SAME builder the chat send path uses. The seed is LIFTED OUT of the history it is already in — the accept path
        //     persisted it before this coordinator ran — or the caller's input goes twice. selectedPath is null: integration conversations never regenerate.
        var history = conversation with
        {
            Messages = [.. conversation.Messages.Where(message => message.MessageId != executionId)]
        };
        //     A CALLER-MANAGED continuation adds one framed document of the session's committed external.output payloads, in the builder's existing
        //     attachmentContext slot: it lands at slot 0, ahead of the synopsis and the verbatim turns, because it is reference material, not a turn.
        var priorOutputs = replaysToolHistory
            ? await BuildPriorOutputsAsync(services, session, executionId, runToken)
            : null;

        //     And the session's own tool history: a caller-managed continuation replays each completed call and its result as real function content, so the
        //     model can tell an action it PERFORMED from prose describing one. Only this policy asks for it; chat is unchanged (R6-1).
        var conversationContext = ConversationContextBuilder.Build(history,
            seed,
            selectedPath: null,
            priorOutputs,
            imageContext: null,
            knowledgeContext: null,
            replaysToolHistory,
            toolResultExcerptChars);

        // 5. The headless package, and emit_output unioned in AFTER the definition's offer ∩ AllowedToolNames and BEFORE the agent is constructed — the seam
        //    ask_user uses. Approval-required tools are NOT stripped, and the approval flag is recomposed through the node policy here (R4-5): ADR 0008.
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
                              .Build(new LocalChatRuntimePackageRequest
                              {
                                  InvocationId = Guid.NewGuid(),
                                  ConversationId = session.ConversationId,
                                  ResolvedSystemPrompt = resolved.ResolvedSystemPrompt,
                                  ConversationContext = conversationContext,
                                  ModelProfile = effectiveModel,
                                  AgentDefinitionVersion = resolved.AgentDefinitionVersion,
                                  ClientNodeId = LocalChatLoopbackDefaults.ClientNodeId,
                                  AllowedTools = offeredTools,
                                  Timeouts = new TimeoutSettings
                                  {
                                      InvocationTimeoutSeconds = nodeSettings.MaxMessageRequestTimeoutSeconds
                                  },
                                  ReasoningEffort = resolved.ReasoningEffort,
                                  SupportsThinking = supportsThinking,
                                  ReasoningBudgetEnforceable = capabilities.ReasoningBudgetEnforceable,
                                  Skills = resolved.Skills,
                                  IsUnattended = true
                              });

        // 6. Subscribe BEFORE the lease, and keep ONE subscription lifetime for the whole run: it cannot miss a terminal report, and it closes after the
        //    drain and after the terminal append, so no event raised during the drain is dropped.
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

        // 6b. The turn's tool parts accumulate through the SAME primitive chat feeds, and only the tool half is fed: an integration run streams no reasoning
        //     deltas to a pump. Subscribed AFTER the mapper's handler — a multicast delegate stops at the first throwing handler, and the mapper owns the caller's stream.
        var parts = new NodeChatPartAccumulator();
        var partSequence = 0L;

        void OnToolCallLifecycleChanged(object? sender, ToolCallLifecycleChangedEventArgs args)
        {
            var payload = args.Payload;
            if (payload.InvocationId != package.InvocationId)
            {
                return;
            }

            if (string.IsNullOrEmpty(payload.ToolCallId))
            {
                // The accumulator keys parts by call id and throws on an empty one, which InvocationRunner's card-id resolution can yield: a payload that
                // cannot be correlated into a call/result pair is dropped rather than allowed to fault the run.
                _logger.LogDebug("Integration execution {ExecutionId} saw a {Phase} lifecycle event for tool {ToolName} with no tool-call id; it is not persisted as a part.",
                    context.ExecutionId,
                    payload.Phase,
                    payload.ToolName);
                return;
            }

            ChatStreamEventMapper.AccumulateToolPart(parts, payload, Interlocked.Increment(ref partSequence));
        }

        dispatcher.InvocationStateChanged += OnInvocationStateChanged;
        dispatcher.InvocationStateChanged += mapper.OnInvocationStateChanged;
        dispatcher.ToolCallLifecycleChanged += mapper.OnToolCallLifecycleChanged;
        dispatcher.ToolCallLifecycleChanged += OnToolCallLifecycleChanged;
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
                parts,
                runToken,
                cancelToken,
                stoppingToken);
        }
        finally
        {
            dispatcher.ToolCallLifecycleChanged -= OnToolCallLifecycleChanged;
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
        NodeChatPartAccumulator parts,
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
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.QueueTimeout, "The execution waited longer than this node's maximum queue age.");
            return;
        }

        // R5-2: the lease wait runs under the remaining budget — the dispatcher's first act is to await its SemaphoreSlim on this token, so the expiry
        // surfaces as an OperationCanceledException with no lease held. The token goes to the lease request and NOWHERE else: the run must not inherit it.
        using var queueDeadline = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        queueDeadline.CancelAfter(TimeSpan.FromMilliseconds(remaining));

        var leaseTask = dispatcher.ReportInvocationAssignedAsync(package, queueDeadline.Token);

        try
        {
            // 7a. A free slot completes the task synchronously, so an incomplete task is an exact, allocation-free "this one had to wait". Accepted straight
            //     to Running is legal; Queued exists only for a real wait, and this is its only producer.
            if (!leaseTask.IsCompleted
                && await store.UpdateStatusAsync(new IntegrationExecutionStatusUpdate
                {
                    ExecutionId = executionId,
                    ExpectedVersion = context.Version,
                    ExpectedStatuses = AcceptedOnly,
                    NewStatus = IntegrationExecutionStatus.Queued
                }, runToken))
            {
                // A false means a concurrent cancel already CASed the row on the same version, so the row is terminal
                // and no execution.queued may follow it.
                context.Version++;
                var queued = _buffer.Append(executionId, session.Id, IntegrationStreamEventTypes.ExecutionQueued, contentType: null, payload: null);
                await store.AppendEventAsync(new IntegrationEventAppend
                {
                    EventId = Guid.NewGuid(),
                    ExecutionId = executionId,
                    Sequence = queued.Sequence,
                    EventType = queued.Type,
                    DetailJson = null,
                    OccurredAtUtc = queued.OccurredAtUtc
                }, runToken);
            }
        }
        catch
        {
            // Cancel BEFORE awaiting the in-flight lease: disposing `queueDeadline` does NOT cancel a pending SemaphoreSlim wait, so the permit would be
            // granted to nobody and hold the node's ONE invocation slot forever. Never a detached task: ADR 0008 ("Invariants the coordinator enforces").
            await queueDeadline.CancelAsync();
            try
            {
                var orphan = await leaseTask;
                await orphan.DisposeAsync();
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
            lease = await leaseTask;
        }
        catch (OperationCanceledException)
        {
            // 7b. Three causes reach here with no lease held, and they must not be conflated.
            await ReloadVersionAsync(context);
            if (cancelToken.IsCancellationRequested)
            {
                await TerminalizeAsync(context, BeforeRunStatuses, IntegrationExecutionStatus.Cancelled, failureCategory: null, failureSummary: null);
            }
            else if (stoppingToken.IsCancellationRequested)
            {
                await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.Shutdown, "The node stopped while the execution was waiting for the invocation lease.");
            }
            else
            {
                await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.QueueTimeout, "The execution waited longer than this node's maximum queue age.");
            }

            return;
        }

        IDisposable? reservation = null;
        try
        {
            // 7b1. A lease acquired at or past the deadline is still a stale run: the caller has been told, or has given up, and the node's only invocation
            //      slot is about to be spent on a result nobody reads. Checked before capacity, before the Running CAS, before any side effect at all.
            if (NowUnixMilliseconds() >= deadlineUtc)
            {
                await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.QueueTimeout, "The execution waited longer than this node's maximum queue age.");
                return;
            }

            // 7b2. Capacity with the lease already HELD, the reverse of the scheduler's order: a queued integration run must not hold a footprint across the
            //      lease wait and fail a concurrent interactive turn's capacity decision. Disposed in reverse acquisition order below.
            var decision = await services.GetRequiredService<ICapacityService>().DecideAsync(effectiveModel, ModelRole.Chat, runToken);
            if (decision.Verdict == CapacityVerdict.RejectInsufficient)
            {
                await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.CapacityRejected, "The node could not reserve capacity for the trigger's model.");
                return;
            }

            reservation = decision.Reservation;

            // 7c. A cancel that arrived while the lease was being awaited lands here.
            var current = await store.GetByIdAsync(executionId, runToken);
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
                await TerminalizeAsync(context, BeforeRunStatuses, IntegrationExecutionStatus.Cancelled, failureCategory: null, failureSummary: null);
                return;
            }

            // 7d. The invocation id is stamped in this same update: the column is the audit row's correlation and
            //     nothing else ever writes it.
            var startedAtUtc = NowUnixMilliseconds();
            if (!await store.UpdateStatusAsync(new IntegrationExecutionStatusUpdate
                    {
                        ExecutionId = executionId,
                        ExpectedVersion = context.Version,
                        ExpectedStatuses = BeforeRunStatuses,
                        NewStatus = IntegrationExecutionStatus.Running,
                        StartedAtUtc = startedAtUtc,
                        EndedAtUtc = null,
                        InvocationId = package.InvocationId
                    },
                    runToken))
            {
                var reloaded = await store.GetByIdAsync(executionId, runToken);
                if (reloaded is null || !NonTerminalStatuses.Contains(reloaded.Status))
                {
                    return;
                }

                context.Version = reloaded.Version;

                // The likely reason this CAS lost: the cancel path stamps its durable stop marker as a NON-terminal status update, which bumps the version
                // without terminalizing. The marker is honoured rather than ignored, or a caller holding a cancel 202 is shown a failure it never caused.
                if (reloaded.StopRequestedAtUtc is not null)
                {
                    await TerminalizeAsync(context, BeforeRunStatuses, IntegrationExecutionStatus.Cancelled, failureCategory: null, failureSummary: null);
                    return;
                }

                await TerminalizeFromFaultAsync(context,
                    IntegrationExecutionStatus.Failed,
                    IntegrationFailureCategories.InternalFailure,
                    $"The execution could not be moved to Running from {reloaded.Status}.");
                return;
            }

            context.Version++;
            context.InvocationId = package.InvocationId;

            // 7e. execution.started, then the assistant placeholder: terminalization correlates on (ConversationId, MessageId, RequestId) against an EXISTING
            //     placeholder row, so creating it after the run would leave the assistant turn unpersisted.
            var started = _buffer.Append(executionId, session.Id, IntegrationStreamEventTypes.ExecutionStarted, contentType: null, payload: null);
            await store.AppendEventAsync(new IntegrationEventAppend
            {
                EventId = Guid.NewGuid(),
                ExecutionId = executionId,
                Sequence = started.Sequence,
                EventType = started.Type,
                DetailJson = null,
                OccurredAtUtc = started.OccurredAtUtc
            }, runToken);

            var correlation = new NodeChatMessageCorrelation
            {
                ConversationId = session.ConversationId,
                MessageId = messageId,
                RequestId = executionId
            };
            _ = await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest
                {
                    ConversationId = session.ConversationId,
                    MessageId = messageId,
                    RequestId = executionId,
                    CreatedAtUtc = startedAtUtc,
                    Model = effectiveModel,
                    MetadataJson = null,
                    Origin = NodeChatOriginValues.Local,
                    AgentDefinitionId = session.AgentDefinitionId
                },
                runToken);

            // 8. Run.
            string? failureCategory = null;
            string? failureSummary = null;
            try
            {
                var runner = services.GetRequiredService<IInvocationRunner>();

                // BOTH are needed: the linked run token stops the generation, but only Cancel() cancels the run's pending tool calls and attributes the turn
                // to CancellationOrigin.User rather than to a bare abort. Registered for the CURRENT run only, and unregistered with it.
                await using var cancelBridge = cancelToken.Register(() => runner.Cancel(package.InvocationId));
                var executionContext = InvocationExecutionContext.CreatePlain(package, Guid.Empty);
                await runner.RunAsync(executionContext, runToken);
            }
            catch (ApprovalUnavailableException approvalUnavailable)
            {
                // Defended even though the runner classifies and reports this one itself rather than letting it out:
                // if a future runner rethrows, the category must not silently collapse into internal-failure.
                failureCategory = IntegrationFailureCategories.ApprovalRequired;
                failureSummary = approvalUnavailable.Message;
            }

            await FinishAsync(services, context, correlation, terminalState.Value, effectiveModel, mapper, parts, failureCategory, failureSummary);
        }
        finally
        {
            // Reverse acquisition order: a leaked reservation wrongly rejects later spawns, and a null one (QueueSameModel) disposes as a no-op. The inner
            // try is not decoration — a throw from the reservation would otherwise skip the lease and starve every later run on the node.
            try
            {
                reservation?.Dispose();
            }
            finally
            {
                await lease.DisposeAsync();
            }
        }
    }

    private async Task FinishAsync(IServiceProvider services,
        ExecutionRunContext context,
        NodeChatMessageCorrelation correlation,
        InvocationState? state,
        string effectiveModel,
        IntegrationStreamEventMapper mapper,
        NodeChatPartAccumulator parts,
        string? failureCategory,
        string? failureSummary)
    {
        var status = IntegrationExecutionStatus.Failed;

        // 9. The assistant turn, from the terminal state, with whatever tool parts the run accumulated. A turn that produced none passes null rather than an
        //    empty list: null is the persistence contract's "leave the existing parts untouched", not a claim that the turn ran no tools.
        if (state is null)
        {
            // The runner returned without reporting. Do not dereference; the row's reason names the case.
            failureCategory ??= IntegrationFailureCategories.InternalFailure;
            failureSummary ??= "The invocation returned without reporting a terminal state.";
            // Tools can have run before the runner went silent, so this branch persists the parts too.
            _ = await services.GetRequiredService<INodeChatPersistenceService>()
                              .TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
                                  {
                                      Correlation = correlation,
                                      Status = NodeChatMessageStatusValues.Failed,
                                      UpdatedAtUtc = NowUnixMilliseconds(),
                                      Parts = parts.HasParts ? parts.Snapshot() : null
                                  },
                                  CancellationToken.None);
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
                // The runner classifies an unattended approval refusal as AgentRuntime and surfaces its own fixed-shape reason verbatim, so the prefix is what
                // separates "this agent needs a capability it cannot have unattended" from "something broke" — the whole reason the tools are not stripped.
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
                                         .ResolveAsync(state.ModelUsed ?? effectiveModel, CancellationToken.None);

            // The envelope is not optional: SummarizeTokenUsageAsync reads only kind-1 rows, and the kind-3 audit row carries a status and a latency, not the
            // token columns. The trailing members mirror NodeChatInvocationPump.TerminalizeAsync, so this surface is measured like every other turn.
            var envelope = new AgentRunEnvelopeMetadata
            {
                InvocationId = state.InvocationId,
                DurationMs = durationMs,
                FailureCategory = state.FailureCategory?.ToString(),
                ContentChunkCount = state.StreamedChunkCount,
                ReasoningChunkCount = state.StreamedThinkingChunkCount,
                TraceId = Activity.Current?.TraceId.ToString(),
                StartedAtUtc = state.StartedAt == default ? null : state.StartedAt.ToUnixTimeMilliseconds(),
                Provider = provider,
                ToolSchemaTokens = state.ToolSchemaTokens,
                MaxToolSchemaTokens = state.MaxToolSchemaTokens,
                DispatchedTier = state.DispatchedTier,
                AuthoredEffort = state.AuthoredEffort,
                ModelReadinessMs = state.ModelReadinessMs,
                TurnInputTokens = state.TurnInputTokens,
                TurnOutputTokens = state.TurnOutputTokens,
                TurnTotalTokens = state.TurnTotalTokens,
                TurnReasoningTokens = state.TurnReasoningTokens
            };

            // Carried to the terminal event: `execution.completed` is `{tokens?, durationMs}`, and this is the one
            // place both numbers exist.
            context.RunDurationMs = durationMs;
            context.TotalTokens = state.TotalTokens;

            _ = await services.GetRequiredService<INodeChatPersistenceService>()
                              .TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
                                  {
                                      Correlation = correlation,
                                      Status = terminalStatus,
                                      UpdatedAtUtc = NowUnixMilliseconds(),
                                      Content = state.StreamedContent,
                                      Reasoning = null,
                                      // A cancelled turn persists NO error text: a cancel is an outcome, not a failure.
                                      Error = terminalStatus == NodeChatMessageStatusValues.Cancelled ? null : state.Error,
                                      Model = state.ModelUsed ?? effectiveModel,
                                      InputCount = state.InputTokens,
                                      OutputCount = state.OutputTokens,
                                      TotalCount = state.TotalTokens,
                                      ReasoningCount = state.ReasoningTokens,
                                      Parts = parts.HasParts ? parts.Snapshot() : null,
                                      GenerationDurationMs = state.GenerationDurationMs,
                                      Envelope = envelope
                                  },
                                  CancellationToken.None);
        }

        // 9b. THE DRAIN SEAM: an undrained tool.* row can land AFTER the terminal event, which a reader that stops on the terminal would never see. It also
        //     latches the handlers shut, so the terminal below is provably the highest sequence; CancellationToken.None, because the ring already published them.
        try
        {
            await mapper.DrainAsync(CancellationToken.None);
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

        // 10. ONE transaction (ruling R5-4): the status CAS, the terminal event at the reserved sequence, and both watermarks. Never Append for a terminal
        //     event — it publishes before the row exists — and never a CAS plus a separate insert: startup recovery scans only NON-terminal rows.
        if (!await TerminalizeAsync(context, RunningOnly, status, failureCategory, failureSummary))
        {
            // A Running row that lost every bounded attempt is stranded: nothing else picks it up, and its admission slot is held until the process restarts.
            // The fault path re-reads over EVERY non-terminal status, honours the stop marker it finds, and returns at once if another writer closed the row.
            await TerminalizeFromFaultAsync(context, status, failureCategory, failureSummary);
        }
    }

    /// <summary>
    ///     Replays the session's committed <c>external.output</c> payloads back to the model as DATA, so a continued run
    ///     can tell a result it already delivered from prose it merely wrote.
    /// </summary>
    /// <remarks>
    ///     Two reads and no new store method: the session's most recent executions newest-first, then each one's
    ///     persisted events. The CURRENT execution is skipped (it has committed nothing yet), as is any row whose
    ///     <c>OutputCount</c> is zero, so a session of pure-prose turns costs one indexed query and no more. Only
    ///     COMMITTED rows are read: a reserved-but-abandoned sequence never became a row, so the replay matches what
    ///     the caller actually received.
    /// </remarks>
    private async Task<ConversationMessageDto?> BuildPriorOutputsAsync(IServiceProvider services,
        IntegrationSessionSnapshot session,
        Guid currentExecutionId,
        CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<IIntegrationExecutionStore>();
        var executions = await store.ListAsync(new IntegrationExecutionFilter
            {
                TriggerId = null,
                SessionId = session.Id,
                Status = null,
                // One MORE than the cap: the current execution occupies a row here and is skipped below, so asking
                // for exactly MaxPayloads would replay seven prior outputs where R4-9(b) promises eight.
                Limit = IntegrationPriorOutputsComposer.MaxPayloads + 1,
                Offset = 0
            },
            cancellationToken);

        var envelopes = new List<string>(IntegrationPriorOutputsComposer.MaxPayloads);
        foreach (var execution in executions)
        {
            if (execution.Id == currentExecutionId || execution.OutputCount == 0)
            {
                continue;
            }

            // simplified: one page, not a paging loop — an execution's persisted rows are bounded by the 40-iteration tool cap (a handful of phase events, at
            // most 80 tool.* and at most 40 external.output). Raise this limit with MaximumToolIterationsPerRequest if that cap is ever raised.
            var events = await store.ListEventsAsync(execution.Id, sinceSequence: 0, limit: 200, cancellationToken);
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
    ///     Closes the session of a terminalized <c>PerInvocation</c> execution, which exists for one run and would
    ///     otherwise show an operator a session nothing will ever join.
    /// </summary>
    /// <remarks>
    ///     A <c>CallerManaged</c> session is closed only by the operator's delete, which is the whole point of the
    ///     policy. Best effort and never fatal: the execution is already terminal and its caller already answered, so a
    ///     failure here is a log line rather than a reason to reopen a committed terminal.
    /// </remarks>
    private async Task ClosePerInvocationSessionAsync(IServiceProvider services, IntegrationExecutionSnapshot execution)
    {
        try
        {
            var row = await services.GetRequiredService<IIntegrationExecutionStore>().GetByIdAsync(execution.Id, CancellationToken.None);
            if (row is null || NonTerminalStatuses.Contains(row.Status))
            {
                return;
            }

            var trigger = await services.GetRequiredService<IIntegrationTriggerStore>().GetByIdAsync(execution.TriggerId, CancellationToken.None);
            if (trigger is null || trigger.SessionPolicy != IntegrationSessionPolicy.PerInvocation)
            {
                return;
            }

            _ = await services.GetRequiredService<IntegrationSessionService>().CloseAsync(execution.SessionId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "The per-invocation session {SessionId} of integration execution {ExecutionId} could not be closed.",
                execution.SessionId,
                execution.Id);
        }
    }

    private static async Task ReloadVersionAsync(ExecutionRunContext context)
    {
        var row = await context.Store.GetByIdAsync(context.ExecutionId, CancellationToken.None);
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
        public ExecutionRunContext(IIntegrationExecutionStore store, IntegrationExecutionSnapshot execution)
        {
            Store = store;
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
