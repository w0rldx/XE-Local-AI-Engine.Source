namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Coder.Tools;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

public sealed class NodeChatStreamService(
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
    IConversationSandboxStager conversationSandboxStager,
    IOptions<KnowledgeBaseOptions> knowledgeOptions,
    IOptions<ChatStreamBudgetOptions> streamBudgetOptions,
    TimeProvider timeProvider,
    IToolApprovalPolicy toolApprovalPolicy,
    ILogger<NodeChatStreamService> logger) : INodeChatStreamService
{
    private const int AgentDefinitionVersion = 1;

    // A cancel that lands BEFORE the invocation itself starts — while the turn is still waiting for the shared
    // collision-queue lease, or between acquiring it and the Streaming transition. Distinct from the runner's own
    // cancellation terminals so a turn stopped in the queue is never reported as a model/invocation timeout.
    private const string PreRunCancelledMessage = "Stopped before the response started (cancelled while queued).";

    // The tools whose presence in a turn's offer means the selected agent can read files through the AgentHome sandbox
    // (the read-only coder tools plus the run_in_agent_home gateway). When any is offered AND the conversation has
    // uploaded attachments, the sandbox is re-staged with this conversation's attachments before the tool loop runs.
    private static readonly HashSet<string> AgentHomeCapableToolNames = new(StringComparer.Ordinal)
    {
        CoderToolDefinition.ListFilesToolName,
        CoderToolDefinition.ReadFileToolName,
        CoderToolDefinition.SearchTextToolName,
        AgentHomeToolDefinition.ToolName
    };

    public IAsyncEnumerable<ChatStreamEvent> SendMessageAsync(NodeChatStreamRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            throw new ArgumentException("Message content must be provided.", nameof(request));
        }

        // The per-send sampling seed rides the wire as a string (precision-safe). Reject a malformed value here, before
        // any streaming begins, rather than silently dropping the override deeper in the invocation mapping.
        if (!SeedValue.TryParse(request.SamplingOptions?.Seed, out _, out var seedError))
        {
            throw new ArgumentException(seedError, nameof(request));
        }

        return SendMessageCoreAsync(request, cancellationToken);
    }

    private async IAsyncEnumerable<ChatStreamEvent> SendMessageCoreAsync(NodeChatStreamRequest request,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var harnessStartedTimestamp = Stopwatch.GetTimestamp();

        var turn = await LoadTurnAsync(request, cancellationToken).ConfigureAwait(false);
        var conversation = turn.Conversation;
        var selectedPath = turn.SelectedPath;

        var trimmedContent = request.Content.Trim();
        var userMessageId = request.UserMessageId.GetValueOrDefault(Guid.NewGuid());
        var assistantMessageId = request.MessageId.GetValueOrDefault(Guid.NewGuid());
        var requestId = request.RequestId.GetValueOrDefault(Guid.NewGuid());
        var correlation = new NodeChatMessageCorrelation(request.ConversationId, assistantMessageId, requestId);
        var sequence = new NodeChatStreamSequence();
        var startedAtUtc = NowUnixMilliseconds();

        var userMessage = await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(request.ConversationId, userMessageId, trimmedContent, startedAtUtc),
            cancellationToken).ConfigureAwait(false);
        yield return ToMessageEvent(ChatStreamEventTypes.UserMessagePersisted, correlation, userMessage, sequence.Next());

        // Resolve the active model and the bound/selected agent BEFORE minting the assistant placeholder, because the
        // placeholder is stamped with the resolved agent's id + display-name snapshot (per-response attribution). The
        // resolve has no dependency on the placeholder (it reads conversation, trimmedContent, selectedPath), so the
        // hoist is safe; the emitted SSE order below is unchanged: UserMessagePersisted -> AssistantPending ->
        // AssistantQueued.
        var resolution = await ResolveTurnAsync(request, conversation, activeModelOverride: null, trimmedContent, cancellationToken).ConfigureAwait(false);

        var assistantPlaceholder = await PersistAssistantPlaceholderAsync(request, resolution, assistantMessageId, requestId, cancellationToken).ConfigureAwait(false);

        // The assistant row now exists as Pending, but run ownership (the pump + runner + their protective finally) is
        // not wired until further below. If the client disconnects in this window — including during the awaited
        // GetEnableToolsAsync / attachment staging before the tasks are created — the iterator is disposed and the row
        // would otherwise sit Pending/Queued until the restart reaper. This guard terminalizes it to Interrupted on any
        // pre-ownership teardown; once ownership is established it becomes a no-op and the pump owns the terminal.
        await using var preOwnershipGuard = new PreOwnershipTerminalizationGuard(persistence, correlation, timeProvider, logger);
        yield return ToMessageEvent(ChatStreamEventTypes.AssistantPending, correlation, assistantPlaceholder, sequence.Next());

        // The operator's node-level "Maximum message request timeout" (Node Settings) is what bounds a single local chat
        // turn — without threading it here the package fell back to TimeoutSettings' own default and a raised setting
        // was silently ignored. Only the invocation timeout is operator-controlled; the tool-call and stream-idle
        // timeouts keep their defaults. When the setting equals the TimeoutSettings default the package — and therefore
        // its config hash — is byte-identical to a package built without an explicit Timeouts.
        //
        // Loaded HERE rather than next to the package build below because the same ceiling is stamped on the queued and
        // streaming events: the browser's stream watchdog must know it before the collision-queue wait, which is the
        // first stretch of the turn where nothing at all arrives on the wire.
        var runtimeNodeSettings = await nodeSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        // The turn is Queued until the collision-queue lease is acquired in RunInvocationAsync; it transitions to
        // Streaming only when the invocation actually starts. This keeps a turn waiting behind another invocation
        // visibly "queued" rather than prematurely "streaming".
        var queuedMessage = await persistence.MarkAssistantQueuedAsync(correlation, NowUnixMilliseconds(), cancellationToken).ConfigureAwait(false);
        if (!string.Equals(queuedMessage.Status, NodeChatMessageStatusValues.Queued, StringComparison.Ordinal))
        {
            // The queued mark was rejected because the row already reached a terminal status — a cancel raced ahead of run
            // ownership (before the cancellation registration below exists). Surface the terminal the row actually holds
            // and abort: never wire a pump/runner for an already-finalized turn. The pre-ownership guard stands down as a
            // no-op (its Interrupted terminalize cannot downgrade the terminal row).
            yield return ToMessageEvent(ChatStreamEventMapper.TerminalEventType(queuedMessage.Status), correlation, queuedMessage, sequence.Next());
            yield break;
        }

        yield return ToMessageEvent(ChatStreamEventTypes.AssistantQueued, correlation, queuedMessage, sequence.Next(),
            invocationTimeoutSeconds: runtimeNodeSettings.MaxMessageRequestTimeoutSeconds);

        // The run/persistence lifecycle is owned by the shared runner, NOT by the client connection. When the
        // client cancellationToken fires on disconnect we must only stop forwarding SSE events to the browser;
        // we must never cancel the run or the persistence pump, otherwise the pump would terminalize the message
        // as interrupted before the runner reported its real terminal of Completed or Failed. runCancellation is
        // therefore deliberately NOT linked to cancellationToken — it is tripped only by a genuine user cancel,
        // the stop button routed through the cancellation registry via CancelNodeChatMessageEndpoint, which also
        // cancels the runner's own loop so the pump persists the true Cancelled terminal.
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
        // Six producers write this sink concurrently: the delta/terminal emits in the invocation-state pump, the
        // streaming-transition emit in RunInvocationAsync (the run-transition), the tool-call lifecycle emits in
        // OnToolCallLifecycleChanged, the turn-notice emits in OnTurnNoticeChanged, the pending-approval emits in
        // OnApprovalRequestedChanged, and the pending-question emits in OnUserQuestionRequestedChanged. It is BOUNDED
        // and never makes a producer wait — on a client disconnect the SSE loop below exits while all six keep
        // writing, which is exactly the case Detach() in this method's finally exists to stop retaining.
        var eventSink = new ChatStreamEventSink(correlation, sequence, streamBudgetOptions.Value, timeProvider);

        // Accumulates the ordered reasoning/tool interleave so the terminal persist can write parts[] (the reload
        // render source). Fed by BOTH producers: the forwarder's tool/notice handlers and the reasoning deltas in the
        // pump loop.
        var parts = new NodeChatPartAccumulator();

        // Subscribe before pre-run notice production (cloud attachment/knowledge withholding) so those notices reach
        // the stream. The scope also covers every pre-ownership exit, preventing handler leaks when staging or package
        // construction fails before the pump/runner teardown exists.
        using var eventSubscription = new ChatStreamEventForwarder(eventDispatcher, correlation, requestId, stateChannel.Writer, eventSink, sequence, parts, timeProvider);

        var toolOffer = await ResolveToolOfferAsync(request, resolution, cancellationToken).ConfigureAwait(false);
        var offerTools = toolOffer.OfferTools;
        var allowedTools = toolOffer.AllowedTools;

        var attachmentsAllowed = AreAttachmentsAllowed(resolution);
        await ReportPreRunNoticesAsync(request, resolution, offerTools, attachmentsAllowed, requestId, cancellationToken).ConfigureAwait(false);

        // Agent mode: when the selected agent can read files through the AgentHome sandbox (its offer includes the
        // read-only coder tools or run_in_agent_home), re-stage the sandbox with THIS conversation's uploaded attachments
        // BEFORE building the turn context, so list_files/read_file/search_text see them under attachments/. The stager
        // returns the exact staged paths (empty when Agent Mode is off, there are no extracted files, OR attachments are
        // withheld from a cloud effective model).
        var isAgentHomeTurn = offerTools && OffersAgentHomeTools(allowedTools);
        var staging = isAgentHomeTurn && attachmentsAllowed
            ? await StageConversationAttachmentsAsync(request.ConversationId, runCancellation.Token).ConfigureAwait(false)
            : null;

        await using var sandboxPreparationLease = staging?.Preparation;
        if (staging?.Error is { } sandboxPreparationError)
        {
            var failedMessage = await TerminalizeAssistantFailureAsync(correlation, sandboxPreparationError).ConfigureAwait(false);
            preOwnershipGuard.TerminalizationHandled();
            yield return ToMessageEvent(ChatStreamEventTypes.AssistantFailed, correlation, failedMessage, sequence.Next());
            yield break;
        }

        var stagedAttachmentPaths = staging?.Preparation?.StagedPaths ?? [];

        var turnContext = await BuildTurnContextAsync(request,
            resolution,
            offerTools,
            attachmentsAllowed,
            stagedAttachmentPaths,
            userMessage.Content,
            runCancellation,
            cancellationToken).ConfigureAwait(false);

        var package = BuildRuntimePackage(request,
            resolution,
            ConversationContextBuilder.Build(conversation, userMessage, selectedPath, turnContext.Attachment, turnContext.Image, turnContext.Knowledge),
            allowedTools,
            runtimeNodeSettings.MaxMessageRequestTimeoutSeconds,
            requestId);
        var preRunDurationMs = Stopwatch.GetElapsedTime(harnessStartedTimestamp).TotalMilliseconds;

        var onTerminal = BuildMemoryExtractionHook(resolution, conversation, userMessage, selectedPath, package);

        Task pumpTask;
        Task runTask;
        using var invocationScope = staging?.Preparation?.EnterInvocationScope();
        pumpTask = invocationStatePump.PumpAsync(stateChannel.Reader,
            eventSink,
            correlation,
            // Stamp the FINAL persisted assistant-message model from the effective model (the pump terminalizes from
            // this requestedModel), so the stored attribution reflects the model that actually ran, not request.Model.
            resolution.EffectiveModel,
            sequence,
            parts,
            onTerminal,
            runCancellation.Token,
            // KB sources that grounded this turn land on the terminal row's metadata_json; null when
            // the turn used no knowledge base.
            turnContext.KnowledgeSources);
        // The AgentHome sandbox is touched only by the tool calls inside the run, so hand the workspace back the moment
        // the run finishes rather than when this whole stream unwinds. Everything after the run — the pump's terminal
        // hook (memory extraction can be a model call of its own), the drain below, and this iterator's own teardown —
        // happens AFTER the client has already seen the terminal event and can have sent its next turn. The lease never
        // queues, so any of that still holding it answers that next turn with "the AgentHome workspace is busy" for a
        // workspace nothing is using. The scope-level disposal above stays as the fallback for a throw before this
        // point; both are idempotent.
        runTask = ReleaseSandboxAfterAsync(RunInvocationAsync(package,
                assistantMessageId,
                stateChannel.Writer,
                eventSink,
                correlation,
                requestId,
                sequence,
                resolution.RequiresInstalledChatModel,
                harnessStartedTimestamp,
                preRunDurationMs,
                runCancellation.Token),
            staging?.Preparation);

        // Ownership is now established: the pump + runner are running and the finally below drives every row to the
        // runner's true terminal, so the pre-ownership guard must stand down (a client disconnect from here on must NOT
        // terminalize — the run keeps going and the pump persists its real terminal).
        preOwnershipGuard.OwnershipEstablished();

        try
        {
            // Forward persisted events to the client. The client cancellationToken stops THIS loop only (e.g. the
            // browser/SignalR stream unsubscribed or disconnected). It does not cancel the run or the pump: those
            // keep going on runCancellation.Token so the runner reaches its real terminal and the pump persists it.
            await foreach (var streamEvent in eventSink.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return streamEvent;
            }
        }
        finally
        {
            // The SSE consumer is gone. Detach FIRST, before draining the tasks below: from here every producer's
            // write is a no-op, so the abandoned stream retains nothing for the (possibly long) remainder of the run.
            // Detach deliberately does not COMPLETE the queue — the pump reads a write fault as a persistence fault
            // and would terminalize the row Failed.
            eventSink.Detach();

            // Do NOT cancel runCancellation here on a client disconnect. Let runTask and pumpTask drain to the
            // runner's true terminal (Completed/Failed/Cancelled) so persistence follows the runner's lifecycle,
            // not the client connection's. A genuine user cancel already tripped runCancellation via the registry.
            //
            // DECISION: because the run keeps going, RunInvocationAsync also holds the collision-slot
            // lease until the runner finishes, so a disconnected mid-run turn keeps the slot alive. Accepted as-is
            // for single-user local — at most one queued turn waits, then both persist correctly. If contended
            // multi-session local ever matters, add an explicit disconnect->cancel path distinct from this SSE
            // unsubscribe; do NOT free the slot from here, which would resurrect the interrupted-terminal bug.
            //
            // IMPORTANT: unsubscribe AFTER awaiting runTask/pumpTask, not before. The runner may fire
            // InvocationStateChanged (the Completed terminal) after the SSE loop exits. If we unsubscribe first,
            // the terminal state never reaches the stateChannel, the pump ends without a terminal, and the message
            // is falsely persisted as interrupted.
            await DrainRunAsync(pumpTask, runTask, runCancellation, eventSubscription, requestId).ConfigureAwait(false);
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
        long harnessStartedTimestamp,
        double preRunDurationMs,
        CancellationToken cancellationToken)
    {
        // Queue behind any in-flight invocation (local or platform) before assigning, rather than failing the
        // turn. The lease holds the shared slot for this run; cancelling while still queued aborts the wait and
        // terminalizes the turn as cancelled below.
        IAsyncDisposable? lease = null;

        try
        {
            var queueStartedTimestamp = Stopwatch.GetTimestamp();
            lease = await eventDispatcher.ReportInvocationAssignedAsync(package, cancellationToken).ConfigureAwait(false);
            var queueDurationMs = Stopwatch.GetElapsedTime(queueStartedTimestamp).TotalMilliseconds;

            // The lease is held => the invocation is actually starting. Transition Queued -> Streaming and emit
            // the streaming event so the client leaves the queued state.
            var streamingMessage = await persistence.MarkAssistantStreamingAsync(correlation, NowUnixMilliseconds(), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(streamingMessage.Status, NodeChatMessageStatusValues.Streaming, StringComparison.Ordinal))
            {
                // The row was finalized (cancelled) before streaming could start. Do not stream into a terminal message or
                // run the model: return so the finally completes the state channel and the pump terminalizes from the row.
                return;
            }

            await eventSink.WriteAsync(ToMessageEvent(ChatStreamEventTypes.AssistantStreaming, correlation, streamingMessage, sequence.Next(),
                                   invocationTimeoutSeconds: package.Timeouts.InvocationTimeoutSeconds),
                               cancellationToken)
                           .ConfigureAwait(false);

            // A "Local runtime default" send that resolved no installed GGUF chat model fails BEFORE any provider
            // invocation with a dedicated category, so the client sees an actionable "pull a model" terminal rather
            // than the stale-id "Provider unreachable.".
            if (requiresInstalledChatModel)
            {
                throw new NoChatModelInstalledException();
            }

            using var context = InvocationExecutionContext.CreatePlain(package,
                messageId,
                harnessStartedTimestamp,
                preRunDurationMs,
                queueDurationMs);
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
            // Classified separately from the generic catch so the terminal SSE carries ModelNotInstalled (not
            // Unexpected/ProviderUnreachable) and the message is the actionable, path-free constant.
            logger.LogWarning(exception, "Local node chat stream had no installed GGUF chat model for the local default. RequestId={RequestId}", requestId);
            await eventDispatcher.ReportInvocationFailedAsync(requestId,
                exception.Message,
                FailureCategory.ModelNotInstalled).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Local node chat stream failed. RequestId={RequestId}", requestId);
            await eventDispatcher.ReportInvocationFailedAsync(requestId,
                "local-chat-stream-failed",
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
    ///     Collects the user turns for extraction: the prior completed user turns on the selected path plus the just-sent
    ///     user turn, ordered. Assistant turns are excluded — the agent's own answer is supplied separately as the run's
    ///     <c>AssistantResponse</c>. Content is held only for the in-scope model call/dedup; it is never persisted here.
    /// </summary>
    private static IReadOnlyList<MemoryExtractionTurn> CollectUserTurns(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto userMessage,
        IReadOnlyDictionary<Guid, Guid>? selectedPath)
    {
        // Order by the variant group's ANCHOR, not the chosen sibling's own sequence, so a late-regenerated early turn
        // is mined in its logical position (SelectedPathResolver.CreateAnchorResolver).
        var anchorSequence = SelectedPathResolver.CreateAnchorResolver(conversation.Messages);
        var selected = SelectedPathResolver.Resolve(conversation.Messages, selectedPath);

        return selected
               .Where(static message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                                        && !string.IsNullOrWhiteSpace(message.Content)
                                        && string.Equals(message.Status, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal))
               .Concat([userMessage])
               .OrderBy(anchorSequence)
               .Select(static message => new MemoryExtractionTurn(message.Content))
               .ToArray();
    }

    // Emits the AttachmentsWithheld notice when a cloud effective model would otherwise have received attachment content
    // but the operator has not opted in — but ONLY when the conversation actually has attachments to withhold, so a
    // plain cloud chat with no attachments stays silent. Reuses the same turn-notice fan-out as the runner's notices.
    private async Task ReportAttachmentsWithheldIfPresentAsync(NodeChatStreamRequest request,
        string? effectiveModel,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var hasAttachments = await turnContextBuilder.HasAttachmentContentAsync(request.ConversationId, request.AttachmentFileIds, cancellationToken).ConfigureAwait(false);
        if (!hasAttachments)
        {
            return;
        }

        await eventDispatcher.ReportTurnNoticeAsync(new TurnNoticePayload
                             {
                                 InvocationId = requestId,
                                 Kind = TurnNoticeKind.AttachmentsWithheld,
                                 Message =
                                     "Your uploaded files were not shared with the cloud model handling this message. Enable cloud data access for this node to allow attachments and file tools to reach a cloud model.",
                                 Detail = effectiveModel
                             })
                             .ConfigureAwait(false);
    }

    // Emits the KnowledgeWithheld notice when the user opted into knowledge grounding for a plain-chat turn but a cloud
    // effective model would have received it without the operator's data-access opt-in. Mirrors the attachments-withheld
    // fan-out; the turn still runs, just without knowledge-base context.
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

    /// <summary>
    ///     Reads the conversation this send belongs to and settles which variant branch shapes its history. A selection
    ///     map on the request is the authoritative, just-clicked path: it is persisted BEFORE the conversation is read.
    ///     That write also CLEARS the stored compaction synopsis (a synopsis built on the previous path can misrepresent
    ///     the newly selected branch), and the turn-scoped read skips exactly the blobs that synopsis covers — so
    ///     reading first would build the turn from a synopsis the database no longer has, on top of history whose
    ///     covered messages were never decrypted. With no map on the request, the selection already persisted on the
    ///     conversation wins.
    /// </summary>
    private async Task<ChatTurnLoad> LoadTurnAsync(NodeChatStreamRequest request, CancellationToken cancellationToken)
    {
        // Reject sends to a remote-origin (view-only) conversation before any persistence happens. The guard is
        // authoritative; throwing here propagates to the hub caller.
        await mutationGuard.EnsureMutableAsync(request.ConversationId, cancellationToken).ConfigureAwait(false);

        var persistedSelectedPath = request.SelectedPath is not null
            ? await persistence.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest(request.ConversationId, request.SelectedPath, NowUnixMilliseconds()), cancellationToken).ConfigureAwait(false)
            : null;

        // Turn-scoped read: same message structure, minus the content/metadata blobs of the non-user messages this
        // conversation's compaction synopsis has already replaced — ConversationContextBuilder.Build drops them by sequence and
        // CollectUserTurns keeps only user roles, so decrypting them was always dead work. Never use this variant for a
        // conversation that will be rendered or re-persisted.
        var conversation = await persistence.GetConversationForTurnAsync(request.ConversationId, cancellationToken).ConfigureAwait(false)
                           ?? throw new NodeChatConversationNotFoundException(request.ConversationId);

        return new ChatTurnLoad(conversation, persistedSelectedPath ?? conversation.SelectedPath);
    }

    // Mints the assistant row this turn streams into. It is stamped with the model that will actually run (the agent pin
    // when honored, the user's explicit dropdown pick when it suppressed the pin, else the local-default) — never the
    // raw request model — so the attribution shown in the UI matches the run from the first pending frame. The effort is
    // stamped with the same precedence as the runtime package (EffectiveReasoningEffort: an agent's pinned effort wins
    // over the request's selection, unless the caller's is a pin too), and it survives reload off the metadata blob.
    private Task<NodeChatPersistedMessageDto> PersistAssistantPlaceholderAsync(NodeChatStreamRequest request,
        ChatTurnResolution resolution,
        Guid assistantMessageId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        return persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(request.ConversationId,
                assistantMessageId,
                requestId,
                NowUnixMilliseconds(),
                resolution.EffectiveModel,
                AgentDefinitionId: resolution.Resolved?.AgentDefinitionId,
                AgentName: resolution.Resolved?.AgentName,
                ReasoningEffort: EffectiveReasoningEffort(request, resolution.Resolved?.ReasoningEffort)),
            cancellationToken);
    }

    /// <summary>
    ///     The effort this turn runs at. A bound agent's pinned effort wins over the one the send carried, because the
    ///     pin is configuration and the send's is the composer's selection — unless the caller says its own is a pin
    ///     too, which is what a development-workflow node's authored effort is.
    /// </summary>
    private static string? EffectiveReasoningEffort(NodeChatStreamRequest request, string? resolvedEffort) =>
        request.ReasoningEffortOverridesAgentPin
            ? request.ReasoningEffort ?? resolvedEffort
            : resolvedEffort ?? request.ReasoningEffort;

    /// <summary>
    ///     Resolves whether this turn offers tools and, if so, which ones travel in the runtime package. Tools are
    ///     offered only when the client asked for them AND the node has the agent tool engine enabled AND the active
    ///     model advertises the tools capability; the invocation factory resolves the matching executables from the
    ///     registry by name. A bound definition narrows the offer to its allowed set (already run through the node
    ///     approval policy, custom tools merged, in <see cref="ChatTurnResolver" />).
    ///     <para>
    ///         The unbound/deleted-agent fallback builds the raw offer here via the async provider (so its custom tools
    ///         merge in too) and applies the SAME node policy (tighten-only) — otherwise a node-wide policy would be
    ///         bypassable by a plain unbound chat turn. With no policy configured the Permissive floor is identity, so
    ///         the fallback offer is byte-identical to the raw catalog offer. A custom tool called on this agentless
    ///         path is not session-approvable (the package carries no CustomTools without a resolved agent), so it
    ///         re-prompts each time — the safe direction.
    ///     </para>
    /// </summary>
    private async Task<ChatToolOffer> ResolveToolOfferAsync(NodeChatStreamRequest request, ChatTurnResolution resolution, CancellationToken cancellationToken)
    {
        var enableTools = await runtimeSettings.GetEnableToolsAsync(cancellationToken).ConfigureAwait(false);
        var offerTools = request.UseLocalTools && enableTools && resolution.SupportsTools;
        if (!offerTools)
        {
            return new ChatToolOffer(OfferTools: false, AllowedTools: null);
        }

        if (resolution.Resolved?.AllowedTools is { } resolvedAllowedTools)
        {
            return new ChatToolOffer(OfferTools: true, resolvedAllowedTools);
        }

        var fallbackOffer = await localToolOfferProvider.GetOfferedToolsAsync(resolution.ActiveModel, resolution.EffectiveModelIsCloud, cancellationToken).ConfigureAwait(false);
        return new ChatToolOffer(OfferTools: true,
        [
            .. fallbackOffer.Select(tool => tool with
            {
                RequiresApproval = toolApprovalPolicy.RequiresApproval(tool.Name, tool.Category, tool.RequiresApproval)
            })
        ]);
    }

    /// <summary>
    ///     Cloud-egress consent: node-local conversation attachments are private data, so they reach a cloud model only
    ///     when the operator opted in (<c>KnowledgeBase:AllowCloudModelAccess</c>). "A cloud model would receive it" is
    ///     not only the orchestrator's own effective model: an ORCHESTRATION broadcasts ONE shared seed to every
    ///     participant (per-participant tool stripping cannot redact content already in the seed), so a single cloud
    ///     PARTICIPANT — even under a local root — must withhold the shared attachment context too. This is the
    ///     load-bearing egress gate; the offer provider additionally withholds the file/knowledge tools for a cloud
    ///     model.
    /// </summary>
    private bool AreAttachmentsAllowed(ChatTurnResolution resolution)
    {
        var anyCloudParticipant = resolution.Orchestration?.AnyParticipantIsCloud ?? false;
        var turnReachesCloud = resolution.EffectiveModelIsCloud || anyCloudParticipant;
        return !turnReachesCloud || knowledgeOptions.Value.AllowCloudModelAccess;
    }

    // The notices produced before the invocation starts, in wire order: the orchestration-degraded notice, then the
    // cloud-egress withhold notices. Both ride the same turn-notice fan-out as the runner's own notices.
    private async Task ReportPreRunNoticesAsync(NodeChatStreamRequest request,
        ChatTurnResolution resolution,
        bool offerTools,
        bool attachmentsAllowed,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        // An Orchestrator agent whose orchestration did not compile runs as a lone single agent. That used to be
        // visible only in a server log, so an operator saw an ordinary answer and no hint that the team never ran. Emit
        // ONE notice naming the typed reason. NotOrchestrated (a Single-kind agent, or no bound agent) has no notice, so
        // the overwhelmingly common path stays silent.
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

        if (attachmentsAllowed)
        {
            return;
        }

        // Name the model the notice is about: the orchestrator's own cloud model when that is what reaches the
        // cloud, otherwise the cloud participant whose presence forced the withhold on an otherwise-local root.
        var cloudModelForNotice = resolution.EffectiveModelIsCloud
            ? resolution.EffectiveModel
            : resolution.Orchestration?.FirstCloudParticipantModel ?? resolution.EffectiveModel;
        await ReportAttachmentsWithheldIfPresentAsync(request, cloudModelForNotice, requestId, cancellationToken).ConfigureAwait(false);

        // KB grounding rides the SAME cloud-egress gate as attachments: when the user opted into knowledge
        // grounding for a plain-chat turn but the turn reaches a cloud model without the operator's data-access
        // opt-in, no retrieval runs and a visible notice names the model. Plain chat only — agent mode uses the
        // gated search_knowledge_base tool (withheld by the offer provider), so this notice is not duplicated there.
        if (request.UseKnowledgeBase && !offerTools)
        {
            await ReportKnowledgeWithheldAsync(cloudModelForNotice, requestId, cancellationToken).ConfigureAwait(false);
        }
    }

    // Re-stages the AgentHome sandbox with THIS conversation's uploaded attachments. Returns the lease the caller must
    // dispose alongside the user-visible reason the turn must fail; a busy workspace yields BOTH (the lease still has to
    // be released). A staging failure is never fatal to the process — it terminalizes this one turn — but a genuine
    // cancel propagates untouched.
    private async Task<SandboxStagingOutcome> StageConversationAttachmentsAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        ConversationSandboxPreparation? preparation = null;
        string? error = null;
        try
        {
            preparation = await conversationSandboxStager.PrepareConversationAttachmentsAsync(conversationId, cancellationToken).ConfigureAwait(false);
            if (preparation is null)
            {
                error = "The AgentHome workspace could not be prepared for this response.";
            }
            else if (preparation.IsBusy)
            {
                error = "The AgentHome workspace is busy. Try again after the current operation finishes.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "AgentHome attachment staging failed for conversation {ConversationId}.", conversationId);
            error = "The AgentHome workspace could not be prepared for this response.";
        }

        return new SandboxStagingOutcome(preparation, error);
    }

    // Terminalizes the assistant row Failed for a pre-run refusal (no invocation ever started, hence the zero-duration
    // content-free envelope). Uses CancellationToken.None deliberately: the row must reach a terminal even when the
    // caller's token has already fired.
    private Task<NodeChatPersistedMessageDto> TerminalizeAssistantFailureAsync(NodeChatMessageCorrelation correlation, string error)
    {
        return persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                NodeChatMessageStatusValues.Failed,
                NowUnixMilliseconds(),
                Error: error,
                Envelope: new AgentRunEnvelopeMetadata(InvocationId: null, DurationMs: 0L)),
            CancellationToken.None);
    }

    // Composes the synthetic context messages prepended to this turn. Two cancellation surfaces are in play and the
    // distinction is load-bearing: the attachment/image reads follow the CLIENT token (a disconnect stops them), while
    // knowledge retrieval follows the RUN token so a user stop aborts the search.
    private async Task<ChatTurnContext> BuildTurnContextAsync(NodeChatStreamRequest request,
        ChatTurnResolution resolution,
        bool offerTools,
        bool attachmentsAllowed,
        IReadOnlyList<string> stagedAttachmentPaths,
        string knowledgeQuery,
        CancellationTokenSource runCancellation,
        CancellationToken cancellationToken)
    {
        // The synthetic prepended context differs by mode: plain chat inlines the extracted text directly; agent mode
        // injects only a short pointer naming the staged files (the agent reads their content through its tools, so the
        // text is not double-fed). The pointer is what stops a weak model from guessing a wrong file name. When
        // attachments are withheld from a cloud effective model, neither path composes anything (staged paths are empty
        // and the plain-chat inline is skipped).
        ConversationMessageDto? attachmentContext;
        // Knowledge-base grounding: a second synthetic context message inlined into plain chat when the user
        // opted in, plus the provenance of the inlined hits so the terminal row records them as sources. Null on
        // every path that does not ground on the knowledge base (agent mode, opt-out, cloud-withheld, empty retrieval).
        ConversationMessageDto? knowledgeContext = null;
        IReadOnlyList<NodeChatMessageSource>? knowledgeSources = null;
        if (offerTools)
        {
            attachmentContext = turnContextBuilder.BuildAgentAttachmentHint(request.ConversationId, stagedAttachmentPaths);
        }
        else if (attachmentsAllowed)
        {
            attachmentContext = await turnContextBuilder.BuildAttachmentContextAsync(request.ConversationId, request.AttachmentFileIds, cancellationToken).ConfigureAwait(false);

            // Plain-chat knowledge grounding runs only for a node-local effective model (attachmentsAllowed already
            // encodes the locality gate). Retrieval failure degrades to no context — the turn still proceeds.
            if (request.UseKnowledgeBase)
            {
                var knowledge = await turnContextBuilder.BuildKnowledgeContextAsync(knowledgeQuery, isRegeneratedTurn: false, runCancellation.Token).ConfigureAwait(false);
                if (knowledge is not null)
                {
                    knowledgeContext = knowledge.Message;
                    knowledgeSources = knowledge.Sources;
                }
            }
        }
        else
        {
            attachmentContext = null;
        }

        // Image parts are attached INDEPENDENTLY of the tool/text branch above so a vision model receives them in plain
        // chat, tool-enabled chat, AND agent mode (the offerTools branch only stages TEXT for the file tools; images have
        // no Markdown to stage and would otherwise be silently dropped). Gated on the same cloud-egress guard as
        // attachments and on the effective model actually being vision-capable.
        ConversationMessageDto? imageContext = null;
        if (attachmentsAllowed && resolution.SupportsVision)
        {
            imageContext = await turnContextBuilder.BuildImageContextAsync(request.ConversationId, request.AttachmentFileIds, cancellationToken).ConfigureAwait(false);
        }

        return new ChatTurnContext(attachmentContext, imageContext, knowledgeContext, knowledgeSources);
    }

    // Assembles the runtime package the invocation runs from. The active-model precedence, the effective-agent
    // resolution and the orchestration spec were all settled up front by ResolveTurnAsync (so the placeholder could be
    // stamped with the resolved agent's attribution); they are reused here unchanged. Only the invocation timeout is
    // operator-controlled — the tool-call and stream-idle timeouts keep their defaults, and when the operator's value
    // equals the TimeoutSettings default the package (and therefore its config hash) is byte-identical to one built
    // without an explicit Timeouts.
    private RuntimePackage BuildRuntimePackage(NodeChatStreamRequest request,
        ChatTurnResolution resolution,
        IReadOnlyList<ConversationMessageDto> conversationContext,
        IReadOnlyList<AllowedToolDto>? allowedTools,
        int invocationTimeoutSeconds,
        Guid requestId)
    {
        var resolved = resolution.Resolved;
        return runtimePackageBuilder.Build(new LocalChatRuntimePackageRequest(requestId,
            request.ConversationId,
            resolved?.ResolvedSystemPrompt ?? LoadResolvedSystemPrompt(localChatOptions.Value),
            conversationContext,
            resolution.EffectiveModel,
            resolved?.AgentDefinitionVersion ?? AgentDefinitionVersion,
            LocalChatLoopbackDefaults.ClientNodeId,
            allowedTools,
            RequestedCapabilities: [LocalChatLoopbackDefaults.RequestedCapability],
            Timeouts: new TimeoutSettings
            {
                InvocationTimeoutSeconds = invocationTimeoutSeconds
            },
            ReasoningEffort: EffectiveReasoningEffort(request, resolved?.ReasoningEffort),
            OrchestrationSpec: resolution.Orchestration?.Spec,
            SupportsThinking: resolution.SupportsThinking,
            SamplingOptions: request.SamplingOptions,
            Skills: resolved?.Skills,
            CustomTools: resolved?.CustomTools,
            ReasoningBudgetEnforceable: resolution.ReasoningBudgetEnforceable,
            // Per-agent opt-out from the send-time tool-relevance filter; not hashed, so an opted-out agent keeps a
            // byte-identical config hash.
            DisableToolRelevanceFilter: resolved?.DisableToolRelevanceFilter ?? false,
            // Model-selection provenance for the runner's reasoning-effort dispatcher; false = pinned, never swap.
            // A work-session step never swaps, whatever its provenance says — and every development-workflow node runs
            // as one. The graph was authored against a model; a node that authors neither a model nor an effort, bound
            // to an agent that pins neither, would otherwise be swap-eligible, and a workflow step silently served by a
            // different model is not a decision the graph's author made. IsWorkSessionTurn is set unconditionally by
            // the work-session supervisor, so it covers those turns whether or not the node authored anything.
            AllowAutoModelSwap: resolution.AllowAutoModelSwap && !request.IsWorkSessionTurn));
    }

    /// <summary>
    ///     The post-run adaptive-memory hook, fired once when the pump persists a Completed/Failed terminal — but ONLY
    ///     when the resolved agent has the playbook enabled AND opts into extraction. Retrieval/injection rides
    ///     <c>PlaybookEnabled</c> alone (already baked into the resolved prompt); <c>MemoryExtractionEnabled</c>
    ///     additionally gates whether this run mines NEW candidates, so a retrieval-only agent still uses its memory but
    ///     learns nothing new — and skips the extraction round-trip entirely. Built here rather than inside the pump so
    ///     it closes over the run context the stream service already holds (resolved agent, conversation temp flag, user
    ///     turns, package config hash) and the pump stays content-free. The dispatch is fire-and-forget (its own scope +
    ///     fresh CT) so it never delays the SSE.
    /// </summary>
    private Action<InvocationState, NodeChatPumpTerminalResult>? BuildMemoryExtractionHook(ChatTurnResolution resolution,
        NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto userMessage,
        IReadOnlyDictionary<Guid, Guid>? selectedPath,
        RuntimePackage package)
    {
        return resolution.Resolved is { PlaybookEnabled: true, MemoryExtractionEnabled: true } memoryAgent
            ? ChatMemoryExtractionHook.Build(memoryExtractionDispatcher,
                memoryAgent,
                conversation.ConversationId,
                conversation.MemoryExcluded,
                package,
                resolution.EffectiveModel,
                () => CollectUserTurns(conversation, userMessage, selectedPath))
            : null;
    }

    // Releases the AgentHome workspace as soon as the invocation itself is over, whatever its outcome, then lets the
    // run's result reach DrainRunAsync unchanged. Disposal is idempotent, so the caller's scope-level disposal remains
    // the fallback.
    private static async Task ReleaseSandboxAfterAsync(Task runTask, ConversationSandboxPreparation? preparation)
    {
        try
        {
            await runTask.ConfigureAwait(false);
        }
        finally
        {
            if (preparation is not null)
            {
                await preparation.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // Drains the run after the SSE consumer is gone. The pump is observed FIRST: on a persistence fault it faults here,
    // so cancel the run rather than let a still-generating runner produce output that can no longer be persisted (a user
    // cancel or a normal completion leaves the pump task completed, not faulted). The event subscription is disposed
    // only once BOTH tasks are observed — the runner may fire InvocationStateChanged (the Completed terminal) after the
    // SSE loop exits, and unsubscribing earlier would leave the pump without a terminal and the row falsely persisted as
    // interrupted.
    private async Task DrainRunAsync(Task pumpTask, Task runTask, CancellationTokenSource runCancellation, IDisposable eventSubscription, Guid requestId)
    {
        try
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
                logger.LogError(exception, "Local node chat stream pump faulted; cancelling the run. RequestId={RequestId}", requestId);
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
                logger.LogDebug(exception, "Local node chat stream run completed with an exception after teardown. RequestId={RequestId}", requestId);
            }
        }
        finally
        {
            eventSubscription.Dispose();
        }
    }

    private static bool OffersAgentHomeTools(IReadOnlyList<AllowedToolDto>? allowedTools)
    {
        return allowedTools is not null && allowedTools.Any(tool => AgentHomeCapableToolNames.Contains(tool.Name));
    }

    // Same resource name AgentInstructionProvider.GetBaseScaffold uses (AI.Agent/Instructions/BaseScaffold.txt); kept
    // as a local literal here to avoid taking a DI dependency on IAgentInstructionProvider in this already-large
    // constructor, mirroring how InstructionsResource is already read directly rather than through the provider.
    private const string BaseScaffoldResourceName = "XE_Local_AI_Engine.AI.Agent.Instructions.BaseScaffold.txt";

    /// <summary>
    ///     Reads the embedded chat prompt for the true null-definition fallback (no bound agent at all) and prepends
    ///     the same versioned base scaffold a resolved, non-opted-out agent definition gets, so an unbound send is
    ///     covered identically to a bound one.
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
    ///     Derives the offer-time active model and the effective agent head, then defers to the shared
    ///     <see cref="ChatTurnResolver" /> for capability/definition/orchestration resolution so the assistant
    ///     placeholder can be stamped with the resolved agent's attribution. The effective-agent precedence is
    ///     <c>request.AgentDefinitionId ?? conversation.AgentDefinitionId ?? (memoized) Default Assistant id</c>;
    ///     resolving the Default Assistant on a cold conversation must NOT throw — a missing seed yields a null id, the
    ///     resolver returns null, and the caller keeps the embedded default persona + full offer + the client
    ///     "Default Assistant" label.
    /// </summary>
    private async Task<ChatTurnResolution> ResolveTurnAsync(NodeChatStreamRequest request,
        NodeChatConversationDto conversation,
        string? activeModelOverride,
        string trimmedContent,
        CancellationToken cancellationToken)
    {
        // Resolve the offer-time active model with the SAME precedence the model list/selection uses
        // (ListLocalModelsEndpoint): an explicit request model first, then the operator's node-default selection
        // (StoredNodeSettings.DefaultModelName), then the static config fallback. Without the node-default step a
        // "Local default" send (request.Model is null) would resolve the static fallback instead of the model the
        // operator set as the node default, so a tool-capable node default would never satisfy the capability gate
        // and run_in_agent_home would be withheld even with a tool-capable model selected.
        string? activeModel;
        var requiresInstalledChatModel = false;
        // The user explicitly picked a concrete model in the chat dropdown when there is no upstream override AND
        // request.Model is a real id (non-blank; the "Local default" sentinel arrives as null/blank). That pick must
        // win over a bound agent's pinned ModelProfile for BOTH the run and the persisted attribution, so it suppresses
        // the pin in the resolve below (honorModelProfile=false) and becomes the effective model directly.
        var userPickedConcreteModel = activeModelOverride is null && !string.IsNullOrWhiteSpace(request.Model);
        if (activeModelOverride is not null)
        {
            activeModel = activeModelOverride;
        }
        else if (!string.IsNullOrWhiteSpace(request.Model))
        {
            // An explicitly picked model (incl. an Ollama model) is honored unchanged — only the local-default path
            // (request.Model null/blank) reroutes through the installed-GGUF resolver below.
            activeModel = request.Model;
        }
        else
        {
            // "Local runtime default": resolve to an installed GGUF (llama.cpp) chat-capable model — never Ollama. The
            // operator's persisted node default is honored only when it is itself an installed GGUF chat model. When no
            // GGUF chat model is installed the resolver returns null; flag the turn so RunInvocationAsync surfaces a
            // clear ModelNotInstalled terminal instead of routing the stale config/node-settings id to a dead provider.
            var nodeSettings = await nodeSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            activeModel = await localDefaultChatModelResolver.ResolveAsync(nodeSettings.DefaultModelName, cancellationToken).ConfigureAwait(false);
            requiresInstalledChatModel = activeModel is null;
        }

        // Effective-agent precedence: the just-clicked per-send selection wins, then the legacy conversation binding,
        // then the seeded Default Assistant (mode-off persona). The default id is memoized for the process lifetime so
        // the mode-off hot path avoids a DB round-trip per send.
        var effectiveAgentId = request.AgentDefinitionId
                               ?? conversation.AgentDefinitionId
                               ?? await defaultAgentProvider.GetDefaultAgentIdAsync(cancellationToken).ConfigureAwait(false);

        // The just-sent user turn is the relevance-retrieval query (inert below the threshold / unbound, so the prompt
        // stays byte-identical). The shared resolver gates thinking/tools by the model's advertised capabilities and
        // resolves the definition + any orchestration spec, returning the effective model both the package and the
        // persisted attribution stamp from.
        return await turnResolver.ResolveAsync(activeModel, requiresInstalledChatModel, effectiveAgentId, trimmedContent, userPickedConcreteModel, cancellationToken).ConfigureAwait(false);
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

    // The conversation this send turn is built from, plus the variant selection that shapes its history.
    private sealed record ChatTurnLoad(NodeChatConversationDto Conversation, IReadOnlyDictionary<Guid, Guid>? SelectedPath);

    // The tool offer for one turn: whether tools are offered at all, and the allow-list that travels in the runtime
    // package (null whenever nothing is offered).
    private sealed record ChatToolOffer(bool OfferTools, IReadOnlyList<AllowedToolDto>? AllowedTools);

    // The outcome of staging a conversation's attachments into the AgentHome sandbox. A busy workspace yields BOTH a
    // lease to dispose and a refusal reason, so neither field implies the other is null.
    private sealed record SandboxStagingOutcome(ConversationSandboxPreparation? Preparation, string? Error);

    // The synthetic context messages prepended to one turn, plus the provenance of any inlined knowledge hits.
    private sealed record ChatTurnContext(
        ConversationMessageDto? Attachment,
        ConversationMessageDto? Image,
        ConversationMessageDto? Knowledge,
        IReadOnlyList<NodeChatMessageSource>? KnowledgeSources);
}
