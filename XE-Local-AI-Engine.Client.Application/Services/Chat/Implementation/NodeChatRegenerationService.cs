namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Default <see cref="INodeChatRegenerationService" />. Reuses the shared runner/pump/dispatcher the local send
///     path uses (<see cref="NodeChatStreamService" />); the only structural differences are that the assistant
///     message is a sibling VARIANT (minted via <see cref="INodeChatPersistenceService.CreateMessageVariantAsync" />,
///     not a fresh placeholder) and the conversation context is built UP TO the parent user turn so the regenerate
///     answers the same question without seeing the original answer or other sibling variants.
/// </summary>
public sealed class NodeChatRegenerationService(
    INodeChatPersistenceService persistence,
    ChatInvocationStatePump invocationStatePump,
    ChatTurnResolver turnResolver,
    INodeChatMutationGuard mutationGuard,
    ILocalChatRuntimePackageBuilder runtimePackageBuilder,
    IInvocationRunner invocationRunner,
    IWorkerEventDispatcher eventDispatcher,
    IOptions<LocalChatAgentOptions> localChatOptions,
    INodeRuntimeSettings runtimeSettings,
    INodeChatStreamCancellationRegistry cancellationRegistry,
    ILocalToolOfferProvider localToolOfferProvider,
    IDefaultAgentProvider defaultAgentProvider,
    INodeSettingsStore nodeSettingsStore,
    ILocalDefaultChatModelResolver localDefaultChatModelResolver,
    IMemoryExtractionDispatcher memoryExtractionDispatcher,
    IChatTurnContextBuilder turnContextBuilder,
    IOptions<KnowledgeBaseOptions> knowledgeOptions,
    IOptions<ChatStreamBudgetOptions> streamBudgetOptions,
    TimeProvider timeProvider,
    IToolApprovalPolicy toolApprovalPolicy,
    ILogger<NodeChatRegenerationService> logger) : INodeChatRegenerationService
{
    private const int AgentDefinitionVersion = 1;

    // Mirrors the send path (NodeChatStreamService.PreRunCancelledMessage): a cancel that lands while the turn is still
    // waiting for the shared collision-queue lease, before the invocation — and therefore its own timeout — ever starts.
    private const string PreRunCancelledMessage = "Stopped before the response started (cancelled while queued).";
    private const string AssistantRole = "assistant";
    private const string UserRole = "user";

    public IAsyncEnumerable<ChatStreamEvent> RegenerateAsync(Guid conversationId,
        Guid originalMessageId,
        string? reasoningEffort = null,
        bool useLocalTools = false,
        bool useKnowledgeBase = false,
        IReadOnlyDictionary<Guid, Guid>? selectedPath = null,
        SamplingOptions? samplingOptions = null,
        CancellationToken cancellationToken = default)
    {
        // Same up-front rejection the send path applies (NodeChatStreamService.SendMessageAsync): the sampling seed
        // rides the wire as a string, so a malformed value is caught here rather than silently dropped deeper in the
        // invocation mapping. A null sampling block always parses, keeping the no-override path unchanged.
        if (!SeedValue.TryParse(samplingOptions?.Seed, out _, out var seedError))
        {
            throw new ArgumentException(seedError, nameof(samplingOptions));
        }

        return RegenerateCoreAsync(conversationId, originalMessageId, reasoningEffort, useLocalTools, useKnowledgeBase, selectedPath, samplingOptions, cancellationToken);
    }

    private async IAsyncEnumerable<ChatStreamEvent> RegenerateCoreAsync(Guid conversationId,
        Guid originalMessageId,
        string? reasoningEffort,
        bool useLocalTools,
        bool useKnowledgeBase,
        IReadOnlyDictionary<Guid, Guid>? requestedSelectedPath,
        SamplingOptions? samplingOptions,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var turn = await LoadRegenerationTurnAsync(conversationId, originalMessageId, requestedSelectedPath, cancellationToken).ConfigureAwait(false);
        var conversation = turn.Conversation;
        var selectedPath = turn.SelectedPath;
        var original = turn.Original;

        var newMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var startedAtUtc = NowUnixMilliseconds();

        // Resolve the model and the effective agent BEFORE minting the variant placeholder, because the variant is
        // stamped with the resolved agent's attribution (id + freshly-snapshotted display name). The resolve reads only
        // conversation/original/selectedPath, so the hoist is safe and the emitted SSE order (AssistantPending ->
        // AssistantQueued) is unchanged.
        var resolution = await ResolveTurnAsync(conversation, original, cancellationToken).ConfigureAwait(false);

        var placeholder = await MintVariantAsync(conversationId, originalMessageId, newMessageId, requestId, startedAtUtc, resolution, reasoningEffort, cancellationToken)
            .ConfigureAwait(false);
        var correlation = new NodeChatMessageCorrelation(conversationId, placeholder.MessageId, requestId);
        var sequence = new NodeChatStreamSequence();

        // The variant row now exists as Pending, but run ownership (the pump + runner + their protective finally) is not
        // wired until further below. If the client disconnects in this window — including during the awaited
        // GetEnableToolsAsync / package build before the tasks are created — the iterator is disposed and the variant
        // would otherwise sit Pending/Queued until the restart reaper. This guard terminalizes it to Interrupted on any
        // pre-ownership teardown; once ownership is established it becomes a no-op and the pump owns the terminal. Shared
        // with the send path (NodeChatStreamService) so both front doors behave identically.
        await using var preOwnershipGuard = new PreOwnershipTerminalizationGuard(persistence, correlation, timeProvider, logger);

        yield return ToMessageEvent(ChatStreamEventTypes.AssistantPending, correlation, placeholder, sequence.Next());

        // The operator's node-level "Maximum message request timeout" (Node Settings) is what bounds a single local
        // chat turn — regenerate is a turn too, so it honors the same setting as the send path. Without this the
        // package fell back to TimeoutSettings' own default and a raised setting was silently ignored. Only the
        // invocation timeout is operator-controlled; the tool-call and stream-idle timeouts keep their defaults.
        // When the setting equals the TimeoutSettings default the package — and therefore its config hash — is
        // byte-identical to a package built without an explicit Timeouts.
        //
        // Loaded HERE rather than next to the package build below because the same ceiling is stamped on the queued and
        // streaming events: the browser's stream watchdog must know it before the collision-queue wait, which is the
        // first stretch of the turn where nothing at all arrives on the wire.
        var runtimeNodeSettings = await nodeSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        // Queued until the collision-queue lease is acquired in RunInvocationAsync; transitions to Streaming only
        // when the invocation actually starts, so a turn waiting behind another invocation reads "queued".
        var queuedMessage = await persistence.MarkAssistantQueuedAsync(correlation, NowUnixMilliseconds(), cancellationToken).ConfigureAwait(false);
        if (!string.Equals(queuedMessage.Status, NodeChatMessageStatusValues.Queued, StringComparison.Ordinal))
        {
            // A cancel raced ahead of run ownership and finalized this variant before it could be queued. Emit the terminal
            // the row actually holds and abort — never run an already-finalized regenerate.
            yield return ToMessageEvent(ChatStreamEventMapper.TerminalEventType(queuedMessage.Status), correlation, queuedMessage, sequence.Next());
            yield break;
        }

        yield return ToMessageEvent(ChatStreamEventTypes.AssistantQueued, correlation, queuedMessage, sequence.Next(),
            invocationTimeoutSeconds: runtimeNodeSettings.MaxMessageRequestTimeoutSeconds);

        // The run/persistence lifecycle is owned by the shared runner, NOT by the client connection (mirrors the send
        // path, NodeChatStreamService): when the client cancellationToken fires on disconnect we must only stop
        // forwarding SSE events to the browser, never cancel the run or the pump — otherwise the pump would terminalize
        // the variant Interrupted before the runner reported its real terminal (Completed/Failed). runCancellation is
        // therefore an UNLINKED source, tripped only by a genuine user cancel routed through the cancellation registry
        // (which also cancels the runner's own loop so the pump persists the true Cancelled terminal).
        using var runCancellation = new CancellationTokenSource();
        using var registration = cancellationRegistry.Register(correlation, () =>
        {
            invocationRunner.Cancel(requestId);
            runCancellation.Cancel();
        });

        var stateChannel = Channel.CreateUnbounded<InvocationState>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        // Six producers write this sink concurrently: the streaming-transition emit in RunInvocationAsync, the
        // delta/terminal emits in the invocation-state pump, the tool-call lifecycle emits in
        // OnToolCallLifecycleChanged, the turn-notice emits in OnTurnNoticeChanged, the pending-approval emits in
        // OnApprovalRequestedChanged, and the pending-question emits in OnUserQuestionRequestedChanged. It is BOUNDED
        // and never makes a producer wait — on a client disconnect the SSE loop below exits while all six keep
        // writing, which is exactly the case Detach() in this method's finally exists to stop retaining.
        var eventSink = new ChatStreamEventSink(correlation, sequence, streamBudgetOptions.Value, timeProvider);

        // Accumulates the ordered reasoning/tool interleave so the regenerated turn persists parts[] (the reload
        // render source), symmetric with the send path. Fed by BOTH producers: the forwarder's tool/notice handlers and
        // the reasoning deltas in the pump loop.
        var parts = new NodeChatPartAccumulator();

        // The same fan-out the send path uses (NodeChatStreamService): invocation-state snapshots to the pump's
        // channel, tool-call / turn-notice / approval / question payloads to the SSE sink. Subscribing HERE — before
        // the awaited GetEnableToolsAsync / pre-run notice production / package build — covers every pre-ownership
        // exit, so a client disconnect in that window cannot leak handlers onto the singleton dispatcher.
        //
        // Disposed on scope exit, which is AFTER the finally below has drained the run: the runner may fire the
        // terminal InvocationStateChanged (the Completed terminal) after the SSE loop exits, and detaching earlier
        // would end the pump with no terminal and falsely persist the variant Interrupted.
        using var eventSubscription = new ChatStreamEventForwarder(eventDispatcher, correlation, requestId, stateChannel.Writer, eventSink, sequence, parts, timeProvider);

        // The active-model precedence, the effective-agent resolution, and the orchestration spec were all computed up
        // front (ResolveTurnAsync) so the variant could be stamped with the resolved agent's attribution; reuse those
        // results here unchanged.
        var activeModel = resolution.ActiveModel;
        var resolved = resolution.Resolved;
        var orchestration = resolution.Orchestration;

        // Both tasks are created back-to-back with no await between them, so they end up either both set or both null; a
        // throw before the package build (e.g. an OCE from the awaited GetEnableToolsAsync on client disconnect) leaves
        // both null and the finally has nothing to drain.
        Task? pumpTask = null;
        Task? runTask = null;

        try
        {
            // Symmetric with the send path (NodeChatStreamService): offer tools to the loopback agent only when the
            // client asked AND the node has the tool engine enabled AND the active model advertises the Ollama tools
            // capability. When offered, the catalog's local tools travel in the runtime package as the offer list; the
            // invocation factory resolves the matching executables from the registry by name. A bound definition narrows
            // that offer to its allowed set (and the resolver already withheld the offer for a non-tools model).
            var enableTools = await runtimeSettings.GetEnableToolsAsync(cancellationToken).ConfigureAwait(false);
            var offerTools = useLocalTools && enableTools && resolution.SupportsTools;
            var allowedTools = offerTools
                ? await ResolveAllowedToolsAsync(activeModel, resolution, cancellationToken).ConfigureAwait(false)
                : null;

            // Parity with the send path: an Orchestrator whose orchestration did not compile reruns as a lone single
            // agent, which used to be visible only in a server log. Emit ONE notice naming the typed reason; a Single-kind
            // agent (NotOrchestrated) has no notice, so the common path stays silent.
            if (resolution.OrchestrationOutcome.DegradationNotice is { } orchestrationDegradedMessage)
            {
                await eventDispatcher.ReportTurnNoticeAsync(new TurnNoticePayload
                                     {
                                         InvocationId = requestId,
                                         Kind = TurnNoticeKind.OrchestrationDegraded,
                                         Message = orchestrationDegradedMessage,
                                         Detail = resolution.OrchestrationOutcome.Reason.ToString()
                                     })
                                     .ConfigureAwait(false);
            }

            // Knowledge-base grounding parity with the send path: a regenerated plain-chat turn honors
            // the same opt-in knowledge grounding + cloud-egress gate the send path applies, so a rerun does not silently
            // lose grounding + its sources strip. Agent mode reaches the KB through the gated search_knowledge_base tool
            // (offerTools), so inline grounding is plain-chat only — mirroring NodeChatStreamService. The retrieval query
            // is the user turn the regenerate re-answers (same cutoff anchor as the regeneration context).
            var knowledge = useKnowledgeBase && !offerTools
                ? await GroundOnKnowledgeBaseAsync(conversation, original, resolution, requestId, runCancellation.Token, cancellationToken).ConfigureAwait(false)
                : null;

            var package = runtimePackageBuilder.Build(new LocalChatRuntimePackageRequest(requestId,
                conversationId,
                resolved?.ResolvedSystemPrompt ?? LoadResolvedSystemPrompt(localChatOptions.Value),
                BuildRegenerationContext(conversation, original, selectedPath, knowledge?.Message),
                resolution.EffectiveModel,
                resolved?.AgentDefinitionVersion ?? AgentDefinitionVersion,
                LocalChatLoopbackDefaults.ClientNodeId,
                allowedTools,
                RequestedCapabilities: [LocalChatLoopbackDefaults.RequestedCapability],
                Timeouts: new TimeoutSettings
                {
                    InvocationTimeoutSeconds = runtimeNodeSettings.MaxMessageRequestTimeoutSeconds
                },
                ReasoningEffort: resolved?.ReasoningEffort ?? reasoningEffort,
                OrchestrationSpec: orchestration?.Spec,
                SupportsThinking: resolution.SupportsThinking,
                // Per-turn sampling overrides, carried exactly as the send path carries them so a regenerated turn
                // reruns under the same knobs the original send used. Null keeps the package byte-identical to today.
                SamplingOptions: samplingOptions,
                Skills: resolved?.Skills,
                CustomTools: resolved?.CustomTools,
                ReasoningBudgetEnforceable: resolution.ReasoningBudgetEnforceable));

            // Post-run adaptive-memory hook (symmetric with the send path): fired once when the pump persists a
            // Completed/Failed terminal, ONLY when the resolved agent has the playbook enabled AND opts into extraction. A
            // regenerated turn is still a completed assistant turn worth learning from — but a retrieval-only agent
            // (extraction off) still uses its memory while mining no new candidates. Built here so it closes over the run
            // context this service holds.
            var onTerminal = resolution.Resolved is { PlaybookEnabled: true, MemoryExtractionEnabled: true } memoryAgent
                ? ChatMemoryExtractionHook.Build(memoryExtractionDispatcher,
                    memoryAgent,
                    conversation.ConversationId,
                    conversation.MemoryExcluded,
                    package,
                    resolution.EffectiveModel,
                    () => CollectUserTurns(conversation, original, selectedPath))
                : null;

            pumpTask = invocationStatePump.PumpAsync(stateChannel.Reader,
                eventSink,
                correlation,
                // Stamp the FINAL persisted variant model from the effective model (the pump terminalizes from this
                // requestedModel) so the stored attribution reflects the model that actually reran, not original.Model.
                resolution.EffectiveModel,
                sequence,
                parts,
                onTerminal,
                runCancellation.Token,
                // Knowledge-base sources that grounded this regenerated turn are stamped onto the
                // variant metadata by the pump. A rerun that used no knowledge base passes none here.
                knowledge?.Sources);
            runTask = RunInvocationAsync(package,
                placeholder.MessageId,
                stateChannel.Writer,
                eventSink,
                correlation,
                requestId,
                sequence,
                resolution.RequiresInstalledChatModel,
                runCancellation.Token);

            // Ownership is now established: the pump + runner are running and the finally below drives every row to the
            // runner's true terminal, so the pre-ownership guard must stand down (a client disconnect from here on must
            // NOT terminalize — the run keeps going and the pump persists its real terminal).
            preOwnershipGuard.OwnershipEstablished();

            // Forward persisted events to the client. The client cancellationToken stops THIS loop only (browser/SignalR
            // disconnect); it does not cancel the run or the pump, which keep going on runCancellation.Token so the
            // runner reaches its real terminal and the pump persists it.
            await foreach (var streamEvent in eventSink.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return streamEvent;
            }
        }
        finally
        {
            // The SSE consumer is gone. Detach FIRST, before draining the tasks below: from here every producer's
            // write is a no-op, so the abandoned stream retains nothing for the remainder of the run. Detach
            // deliberately does not COMPLETE the queue — the pump reads a write fault as a persistence fault and
            // would terminalize the variant Failed.
            eventSink.Detach();

            // Do NOT cancel runCancellation here on a client disconnect: let runTask/pumpTask drain to the runner's true
            // terminal so persistence follows the runner's lifecycle, not the client connection's. A genuine user cancel
            // already tripped runCancellation via the registry.
            if (pumpTask is not null && runTask is not null)
            {
                await DrainRunAsync(pumpTask, runTask, runCancellation, requestId).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Reads the conversation this regenerate reruns and settles which variant branch shapes its history, then
    ///     locates the assistant turn being replaced. Mirrors the send path's own load
    ///     (<c>NodeChatStreamService.LoadTurnAsync</c>), including the selection write ordering below.
    /// </summary>
    private async Task<RegenerationTurnLoad> LoadRegenerationTurnAsync(Guid conversationId,
        Guid originalMessageId,
        IReadOnlyDictionary<Guid, Guid>? requestedSelectedPath,
        CancellationToken cancellationToken)
    {
        // Reject regeneration on a remote-origin (view-only) conversation before any persistence. Authoritative
        // guard; throwing here propagates to the hub caller, same as the send path.
        await mutationGuard.EnsureMutableAsync(conversationId, cancellationToken).ConfigureAwait(false);

        // Persist a request-supplied selection BEFORE reading the conversation. The write also CLEARS the stored
        // compaction synopsis (a synopsis built on the previous path can misrepresent the newly selected branch), so a
        // DTO read first would still carry a synopsis the database no longer has — and BuildRegenerationContext would
        // splice that stale summary in AND drop the verbatim messages it claims to cover.
        var persistedSelectedPath = requestedSelectedPath is not null
            ? await persistence.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest(conversationId, requestedSelectedPath, NowUnixMilliseconds()), cancellationToken).ConfigureAwait(false)
            : null;

        var conversation = await persistence.GetConversationAsync(conversationId, cancellationToken).ConfigureAwait(false)
                           ?? throw new NodeChatConversationNotFoundException(conversationId);

        var original = conversation.Messages.FirstOrDefault(message => message.MessageId == originalMessageId)
                       ?? throw new NodeChatMessageNotFoundException(originalMessageId);

        // Same precedence as the send path: a request-supplied selection is persisted and used; otherwise the
        // already-persisted conversation selection drives the pre-cutoff context.
        return new RegenerationTurnLoad(conversation, persistedSelectedPath ?? conversation.SelectedPath, original);
    }

    // Reuses the backend mint: creates the sibling placeholder (pending, shared variant_group_id, parent copied from the
    // original) — never an in-place overwrite. We do NOT duplicate mint logic here. The variant carries the resolved
    // agent's attribution so the pending variant already shows the agent name.
    private async Task<NodeChatPersistedMessageDto> MintVariantAsync(Guid conversationId,
        Guid originalMessageId,
        Guid newMessageId,
        Guid requestId,
        long startedAtUtc,
        ChatTurnResolution resolution,
        string? reasoningEffort,
        CancellationToken cancellationToken)
    {
        var variant = await persistence.CreateMessageVariantAsync(new NodeChatCreateMessageVariantRequest(conversationId,
                              originalMessageId,
                              newMessageId,
                              requestId,
                              startedAtUtc,
                              // Stamp the variant with the model that will actually rerun (agent pin when honored, the
                              // original turn's explicit pick when it suppressed the pin, else the local-default) — not
                              // the raw original model — so the variant's attribution matches the rerun.
                              resolution.EffectiveModel,
                              AgentDefinitionId: resolution.Resolved?.AgentDefinitionId,
                              AgentName: resolution.Resolved?.AgentName,
                              // Persist the effort that actually drives this regenerated variant — an agent's pinned
                              // effort wins over the regenerate request's selection (same precedence as the runtime
                              // package built for the rerun). Survives reload off the metadata blob.
                              ReasoningEffort: resolution.Resolved?.ReasoningEffort ?? reasoningEffort),
                          cancellationToken).ConfigureAwait(false)
                      ?? throw new NodeChatMessageNotFoundException(originalMessageId);

        return variant.Variant;
    }

    /// <summary>
    ///     The tools that travel in the runtime package for a turn that offers them. A bound definition's AllowedTools
    ///     already ran through the node approval policy in <see cref="ChatTurnResolver" /> (custom tools merged there);
    ///     the unbound/deleted-agent fallback builds the raw offer here via the async provider (custom tools merge in
    ///     too) and applies the SAME node policy (tighten-only) to avoid a bypass. Permissive floor = identity, so an
    ///     unconfigured node stays byte-identical to the raw catalog offer. A custom tool on this agentless path is not
    ///     session-approvable (no resolved agent → no package CustomTools), so it re-prompts each time.
    /// </summary>
    private async Task<IReadOnlyList<AllowedToolDto>> ResolveAllowedToolsAsync(string? activeModel, ChatTurnResolution resolution, CancellationToken cancellationToken)
    {
        if (resolution.Resolved?.AllowedTools is { } resolvedAllowedTools)
        {
            return resolvedAllowedTools;
        }

        var fallbackOffer = await localToolOfferProvider.GetOfferedToolsAsync(activeModel, resolution.EffectiveModelIsCloud, cancellationToken).ConfigureAwait(false);
        return
        [
            .. fallbackOffer.Select(tool => tool with
            {
                RequiresApproval = toolApprovalPolicy.RequiresApproval(tool.Name, tool.Category, tool.RequiresApproval)
            })
        ];
    }

    /// <summary>
    ///     The knowledge-base grounding for a regenerated PLAIN-CHAT turn, composed by the shared
    ///     <see cref="IChatTurnContextBuilder" /> so a rerun grounds byte-identically to the send that produced the
    ///     original. The retrieval query is the user turn the regenerate re-answers (same cutoff anchor as the
    ///     regeneration context). Returns <see langword="null" /> when the egress gate withholds grounding, when there
    ///     is no preceding user turn, or when retrieval produced nothing.
    /// </summary>
    private async Task<KnowledgeChatGrounding?> GroundOnKnowledgeBaseAsync(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto original,
        ChatTurnResolution resolution,
        Guid requestId,
        CancellationToken runCancellationToken,
        CancellationToken cancellationToken)
    {
        // The KB egress gate mirrors attachments: an ORCHESTRATION broadcasts one shared seed to every
        // participant, so a single cloud participant — even under a local root — forces the withhold too.
        var anyCloudParticipant = resolution.Orchestration?.AnyParticipantIsCloud ?? false;
        var turnReachesCloud = resolution.EffectiveModelIsCloud || anyCloudParticipant;
        var knowledgeAllowed = !turnReachesCloud || knowledgeOptions.Value.AllowCloudModelAccess;
        if (!knowledgeAllowed)
        {
            // Name the model the notice is about: the effective cloud model when that is what reaches the cloud,
            // otherwise the cloud participant whose presence forced the withhold on an otherwise-local root.
            var cloudModelForNotice = resolution.EffectiveModelIsCloud
                ? resolution.EffectiveModel
                : resolution.Orchestration?.FirstCloudParticipantModel ?? resolution.EffectiveModel;
            await ReportKnowledgeWithheldAsync(cloudModelForNotice, requestId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var retrievalQuery = ResolvePrecedingUserTurnContent(conversation, original);
        return string.IsNullOrWhiteSpace(retrievalQuery)
            ? null
            : await turnContextBuilder.BuildKnowledgeContextAsync(retrievalQuery, isRegeneratedTurn: true, runCancellationToken).ConfigureAwait(false);
    }

    // Drains the run after the SSE consumer is gone. The pump is observed FIRST: on a persistence fault it faults here,
    // so cancel the run rather than let a still-generating runner produce output that can no longer be persisted (a user
    // cancel or a normal completion leaves the pump task completed, not faulted).
    private async Task DrainRunAsync(Task pumpTask, Task runTask, CancellationTokenSource runCancellation, Guid requestId)
    {
        try
        {
            await pumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The cancelled/interrupted terminal is persisted by the pump.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Local node chat regeneration pump faulted; cancelling the run. RequestId={RequestId}", requestId);
            await runCancellation.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The runner unwound on cancellation; its terminal is persisted by the pump.
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Local node chat regeneration run completed with an exception after teardown. RequestId={RequestId}", requestId);
        }
    }

    private async Task RunInvocationAsync(RuntimePackage package,
        Guid messageId,
        ChannelWriter<InvocationState> stateWriter,
        IChatStreamEventSink eventSink,
        NodeChatMessageCorrelation correlation,
        Guid requestId,
        NodeChatStreamSequence sequence,
        bool requiresInstalledChatModel,
        CancellationToken cancellationToken)
    {
        // Queue behind any in-flight invocation (local or platform) under the shared lease, rather than failing
        // the turn; the lease holds the slot for this run. Cancelling while queued aborts the wait and the run
        // is terminalized as cancelled below.
        IAsyncDisposable? lease = null;

        try
        {
            lease = await eventDispatcher.ReportInvocationAssignedAsync(package, cancellationToken).ConfigureAwait(false);

            var streamingMessage = await persistence.MarkAssistantStreamingAsync(correlation, NowUnixMilliseconds(), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(streamingMessage.Status, NodeChatMessageStatusValues.Streaming, StringComparison.Ordinal))
            {
                // The variant was finalized (cancelled) before streaming could start. Do not stream into a terminal
                // message or run the model: return so the finally completes the state channel and the pump terminalizes.
                return;
            }

            await eventSink.WriteAsync(ToMessageEvent(ChatStreamEventTypes.AssistantStreaming, correlation, streamingMessage, sequence.Next(),
                                   invocationTimeoutSeconds: package.Timeouts.InvocationTimeoutSeconds),
                               cancellationToken)
                           .ConfigureAwait(false);

            // Symmetric with the send path: a regenerate of a "Local runtime default" turn that resolved no installed
            // GGUF chat model fails BEFORE any provider invocation with the dedicated ModelNotInstalled category.
            if (requiresInstalledChatModel)
            {
                throw new NoChatModelInstalledException();
            }

            using var context = InvocationExecutionContext.CreatePlain(package, messageId);
            await invocationRunner.RunAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await eventDispatcher.ReportInvocationFailedAsync(requestId,
                PreRunCancelledMessage,
                FailureCategory.Cancelled).ConfigureAwait(false);
        }
        catch (NoChatModelInstalledException exception)
        {
            // Classified separately so the terminal SSE carries ModelNotInstalled (not Unexpected/ProviderUnreachable).
            logger.LogWarning(exception, "Local node chat regeneration had no installed GGUF chat model for the local default. RequestId={RequestId}", requestId);
            await eventDispatcher.ReportInvocationFailedAsync(requestId,
                exception.Message,
                FailureCategory.ModelNotInstalled).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Local node chat regeneration failed. RequestId={RequestId}", requestId);
            await eventDispatcher.ReportInvocationFailedAsync(requestId,
                "local-chat-regeneration-failed",
                FailureCategory.Unexpected).ConfigureAwait(false);
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }

            stateWriter.TryComplete();
        }
    }

    /// <summary>
    ///     Builds the regeneration context: every completed message UP TO AND INCLUDING the USER turn that precedes
    ///     the turn being regenerated, excluding the original assistant answer and any sibling variants. Assistant
    ///     placeholders are minted with no parent_message_id (the variant's parent is the prior assistant, not the
    ///     user turn), so a parent walk cannot reach the user turn. Instead the cutoff is the latest USER turn
    ///     strictly before the EARLIEST member of the original's variant group — every member of that group (the
    ///     original answer and all sibling variants) sorts at or after that user turn and is therefore excluded.
    ///     When no preceding user turn exists, falls back to everything strictly before the earliest group member.
    /// </summary>
    /// <param name="applyCompaction">
    ///     False only for the memory-extraction turn collection, which mines REAL user turns: it must keep the turns a
    ///     synopsis covers and must never mine the synthetic synopsis message itself (the send path's own
    ///     <c>CollectUserTurns</c> is likewise compaction-free).
    /// </param>
    private static IReadOnlyList<ConversationMessageDto> BuildRegenerationContext(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto original,
        IReadOnlyDictionary<Guid, Guid>? selectedPath,
        ConversationMessageDto? knowledgeContext = null,
        bool applyCompaction = true)
    {
        // Everything here — the cutoff, the compaction splice and the final ordering — runs in ANCHOR space (each
        // group's earliest member sequence), never on a chosen sibling's own sequence. A sibling minted by
        // regenerating an EARLY turn after later turns exist carries a raw sequence PAST them, so a raw
        // `Sequence <= cutoff` filter would drop that turn from context entirely. See
        // SelectedPathResolver.CreateAnchorResolver. With no variants anchor == raw sequence, so a persisted
        // CompactionSummaryCoversToSequence written before this change stays valid.
        var anchorSequence = SelectedPathResolver.CreateAnchorResolver(conversation.Messages);
        var cutoffSequence = ResolvePrecedingUserTurnCutoff(conversation, original, anchorSequence);

        // A prior turn before the cutoff may itself have variants; collapse those to the selected path so the
        // regenerate sees the same chosen branch the send path would. The group being regenerated already sorts
        // at/after the cutoff (see ResolvePrecedingUserTurnCutoff), so it is excluded by the sequence filter
        // regardless of which member the resolver would otherwise pick.
        var selected = SelectedPathResolver.Resolve(conversation.Messages, selectedPath);

        // The synthetic context messages (knowledge-base grounding, then the compaction synopsis) are prepended so the
        // model reads them before the conversation history — same order and rationale as the send path
        // (ConversationContextBuilder.Build). They take the first slots and the history shifts down by
        // their count; empty on a plain, uncompacted rerun.
        var leadingContext = new List<ConversationMessageDto>(capacity: 2);
        if (knowledgeContext is not null)
        {
            leadingContext.Add(knowledgeContext with
            {
                SortOrder = 0
            });
        }

        // Non-destructive compaction, spliced through the same resolver the send path uses: the synopsis replaces the
        // messages it covers instead of re-sending them verbatim. Only when the covered sequence sits BELOW the cutoff —
        // a synopsis that already covers the user turn being answered would leave the rerun with no question at all, so
        // that (compact-then-regenerate-an-older-turn) case keeps the verbatim pre-cutoff history.
        if (applyCompaction && CompactionContextResolver.Resolve(conversation, leadingContext.Count) is { } compaction && compaction.CoveredSequence < cutoffSequence)
        {
            leadingContext.Add(compaction.Summary);
            selected = [.. selected.Where(message => anchorSequence(message) > compaction.CoveredSequence)];
        }

        var messages = selected
                       .Where(message => anchorSequence(message) <= cutoffSequence
                                         && !string.IsNullOrWhiteSpace(message.Content)
                                         && string.Equals(message.Status, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal))
                       .OrderBy(anchorSequence)
                       .Select((message, index) => new ConversationMessageDto
                       {
                           Id = message.MessageId,
                           Role = string.Equals(message.Role, AssistantRole, StringComparison.OrdinalIgnoreCase) ? MessageRole.Assistant : MessageRole.User,
                           Content = message.Content,
                           Thinking = message.Reasoning,
                           ModelUsed = message.Model,
                           SortOrder = index + leadingContext.Count
                       })
                       .ToList();

        return leadingContext.Count == 0 ? messages : [.. leadingContext, .. messages];
    }

    /// <summary>
    ///     Resolves the cutoff sequence (inclusive) for the regeneration context: the latest USER turn strictly
    ///     before the earliest member of the original's variant group. The group spans the original answer and every
    ///     sibling variant, so anchoring on its earliest member keeps all of them out of context whichever member is
    ///     being regenerated. With no preceding user turn, returns the slot before the earliest group member so the
    ///     context is everything that came before — never the answer being replaced.
    /// </summary>
    private static int ResolvePrecedingUserTurnCutoff(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto original,
        Func<NodeChatPersistedMessageDto, int> anchorSequence)
    {
        var precedingUserTurn = ResolvePrecedingUserTurn(conversation, original, anchorSequence);
        return precedingUserTurn is not null ? anchorSequence(precedingUserTurn) : anchorSequence(original) - 1;
    }

    /// <summary>
    ///     The latest USER turn anchored strictly before the original's variant group. The group's anchor IS the
    ///     earliest member's sequence (<see cref="SelectedPathResolver.CreateAnchorResolver{TMessage}" />), so every
    ///     member — the original answer and each sibling variant — anchors at or after it and is excluded whichever
    ///     member is being regenerated.
    /// </summary>
    private static NodeChatPersistedMessageDto? ResolvePrecedingUserTurn(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto original,
        Func<NodeChatPersistedMessageDto, int> anchorSequence)
    {
        var groupAnchor = anchorSequence(original);

        return conversation.Messages
                           .Where(message => anchorSequence(message) < groupAnchor
                                             && string.Equals(message.Role, UserRole, StringComparison.OrdinalIgnoreCase))
                           .OrderByDescending(anchorSequence)
                           .FirstOrDefault();
    }

    /// <summary>
    ///     Collects the user turns for extraction from the regeneration context (pre-cutoff, selected-path collapsed,
    ///     excluding the original answer and its sibling variants), filtered to user-role turns. The agent's regenerated
    ///     answer is supplied separately as the run's <c>AssistantResponse</c>. Content is held only for the in-scope
    ///     model call/dedup; it is never persisted here.
    /// </summary>
    private static IReadOnlyList<MemoryExtractionTurn> CollectUserTurns(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto original,
        IReadOnlyDictionary<Guid, Guid>? selectedPath)
    {
        return BuildRegenerationContext(conversation, original, selectedPath, knowledgeContext: null, applyCompaction: false)
               .Where(static message => message.Role == MessageRole.User && !string.IsNullOrWhiteSpace(message.Content))
               .Select(static message => new MemoryExtractionTurn(message.Content))
               .ToArray();
    }

    // Same resource name AgentInstructionProvider.GetBaseScaffold uses (AI.Agent/Instructions/BaseScaffold.txt); kept
    // as a local literal here (mirrors NodeChatStreamService) to avoid taking a DI dependency on
    // IAgentInstructionProvider in this already-large constructor.
    private const string BaseScaffoldResourceName = "XE_Local_AI_Engine.AI.Agent.Instructions.BaseScaffold.txt";

    /// <summary>
    ///     Reads the embedded chat prompt for the true null-definition fallback (no bound agent at all) and prepends
    ///     the same versioned base scaffold a resolved, non-opted-out agent definition gets, so an unbound regenerate
    ///     is covered identically to a bound one.
    /// </summary>
    private static string LoadResolvedSystemPrompt(LocalChatAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.InstructionsResource))
        {
            throw new ArgumentException("Instructions resource must be provided.", nameof(options));
        }

        var persona = LoadEmbeddedResource(options.InstructionsResource);
        var scaffold = LoadEmbeddedResource(BaseScaffoldResourceName);
        return string.IsNullOrWhiteSpace(scaffold) ? persona : $"{scaffold.TrimEnd()}\n\n{persona}";
    }

    private static string LoadEmbeddedResource(string resourceName)
    {
        var assembly = typeof(LocalChatAgentOptions).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Embedded instructions resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    ///     Derives the offer-time active model and the effective agent head for a regenerate, then defers to the shared
    ///     <see cref="ChatTurnResolver" /> for capability/definition/orchestration resolution. The effective-agent
    ///     precedence reuses the ORIGINAL turn's recorded agent so a rerun stays on the same persona:
    ///     <c>original.AgentDefinitionId ?? conversation.AgentDefinitionId ?? (memoized) Default Assistant id</c>. The
    ///     attribution name is re-resolved (picks up a rename); when the agent was deleted the resolver returns null and
    ///     the variant falls back to the original's stored name. The relevance-retrieval query is the user turn that
    ///     precedes the regenerated turn (same cutoff anchor as the regeneration context).
    /// </summary>
    private async Task<ChatTurnResolution> ResolveTurnAsync(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto original,
        CancellationToken cancellationToken)
    {
        // Mirror the send path. An explicit original-turn model (the operator picked a specific model, incl. an Ollama
        // model) is reused unchanged. A regenerate of a "Local runtime default" turn (original.Model null/blank)
        // re-resolves through the installed-GGUF resolver — never Ollama — so a stale config/node-settings id is never
        // routed to a dead provider; a null result flags the turn for a clear ModelNotInstalled terminal below.
        // Mirror the send path's explicit-pick semantics: the original turn carries a concrete model only when the
        // operator picked one (the "Local runtime default" turn persisted a null/blank model). A concrete original
        // model is an explicit pick that must win over a bound agent's pinned ModelProfile for BOTH the rerun and the
        // variant's attribution, so it suppresses the pin (honorModelProfile=false) and becomes the effective model.
        string? activeModel;
        var requiresInstalledChatModel = false;
        var userPickedConcreteModel = !string.IsNullOrWhiteSpace(original.Model);
        if (userPickedConcreteModel)
        {
            activeModel = original.Model;
        }
        else
        {
            var nodeSettings = await nodeSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            activeModel = await localDefaultChatModelResolver.ResolveAsync(nodeSettings.DefaultModelName, cancellationToken).ConfigureAwait(false);
            requiresInstalledChatModel = activeModel is null;
        }

        // A regenerate reuses the ORIGINAL turn's agent so the rerun stays on the same persona; fall back to the
        // conversation binding, then the seeded Default Assistant (process-memoized id).
        var effectiveAgentId = original.AgentDefinitionId
                               ?? conversation.AgentDefinitionId
                               ?? await defaultAgentProvider.GetDefaultAgentIdAsync(cancellationToken).ConfigureAwait(false);

        var retrievalQuery = ResolvePrecedingUserTurnContent(conversation, original);

        return await turnResolver.ResolveAsync(activeModel, requiresInstalledChatModel, effectiveAgentId, retrievalQuery, userPickedConcreteModel, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     The content of the latest USER turn strictly before the original's variant group — the question the
    ///     regenerate re-answers, used as the relevance-retrieval query. Mirrors the cutoff anchor used by
    ///     <see cref="ResolvePrecedingUserTurnCutoff" />; returns <c>null</c> when no such user turn exists, so the
    ///     resolver falls back to the full static prepend.
    /// </summary>
    private static string? ResolvePrecedingUserTurnContent(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto original) =>
        ResolvePrecedingUserTurn(conversation, original, SelectedPathResolver.CreateAnchorResolver(conversation.Messages))?.Content;

    // Emits the KnowledgeWithheld notice when the user opted into knowledge grounding for a regenerated plain-chat turn
    // but a cloud effective model would have received it without the operator's data-access opt-in. Mirrors the send
    // path (NodeChatStreamService.ReportKnowledgeWithheldAsync); the rerun still runs, just without knowledge context.
    private async Task ReportKnowledgeWithheldAsync(string? effectiveModel, Guid requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await eventDispatcher.ReportTurnNoticeAsync(new TurnNoticePayload
                             {
                                 InvocationId = requestId,
                                 Kind = TurnNoticeKind.KnowledgeWithheld,
                                 Message =
                                     "Your knowledge base was not searched for this message because it is handled by a cloud model. Enable cloud data access for this node to allow knowledge-base grounding to reach a cloud model.",
                                 Detail = effectiveModel
                             })
                             .ConfigureAwait(false);
    }

    private ChatStreamEvent ToMessageEvent(string type,
        NodeChatMessageCorrelation correlation,
        NodeChatPersistedMessageDto message,
        long sequence,
        int? inputTokens = null,
        int? outputTokens = null,
        int? totalTokens = null,
        int? reasoningTokens = null,
        int? invocationTimeoutSeconds = null)
    {
        return ChatStreamEventMapper.MessageEvent(type, correlation, message, NowUnixMilliseconds(), sequence, inputTokens, outputTokens, totalTokens, reasoningTokens,
            invocationTimeoutSeconds);
    }

    private long NowUnixMilliseconds()
    {
        return timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }

    // The conversation this regenerate reruns, the variant branch that shapes its history, and the assistant turn being
    // replaced. Mirrors the send path's ChatTurnLoad, plus the original the cutoff anchors on.
    private sealed record RegenerationTurnLoad(NodeChatConversationDto Conversation, IReadOnlyDictionary<Guid, Guid>? SelectedPath, NodeChatPersistedMessageDto Original);
}
