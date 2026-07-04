namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Coder.Tools;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;

public sealed class NodeChatStreamService(
    INodeChatPersistenceService persistence,
    INodeChatInvocationPump invocationPump,
    INodeChatMutationGuard mutationGuard,
    ILocalChatRuntimePackageBuilder runtimePackageBuilder,
    IInvocationRunner invocationRunner,
    IWorkerEventDispatcher eventDispatcher,
    IOptions<LocalChatAgentOptions> localChatOptions,
    INodeRuntimeSettings runtimeSettings,
    INodeChatStreamCancellationRegistry cancellationRegistry,
    ILocalToolOfferProvider localToolOfferProvider,
    IAgentDefinitionResolver agentDefinitionResolver,
    IAgentDefinitionStore agentDefinitionStore,
    IDefaultAgentProvider defaultAgentProvider,
    IOrchestrationResolver orchestrationResolver,
    INodeSettingsStore nodeSettingsStore,
    IModelClassificationService modelClassificationService,
    ILocalModelProviderResolver localModelProviderResolver,
    IGgufModelCapabilityResolver ggufModelCapabilityResolver,
    ILocalDefaultChatModelResolver localDefaultChatModelResolver,
    ICloudCredentialStore cloudCredentialStore,
    IMemoryExtractionDispatcher memoryExtractionDispatcher,
    IConversationUploadedFileStore uploadedFileStore,
    IConversationSandboxStager conversationSandboxStager,
    TimeProvider timeProvider,
    ILogger<NodeChatStreamService> logger) : INodeChatStreamService
{
    private const int AgentDefinitionVersion = 1;

    // Mid-stream partial flushes (the per-chunk DB read-modify-write + AssistantDelta SSE) are debounced to at most one
    // per this window so a fast local model does not drive one persistence round-trip per token. Terminal/error states
    // always flush immediately, so the final content is never delayed; a crash mid-stream therefore loses at most one
    // window of streamed tokens, which is the same crash-consistency bound the per-chunk flush already accepted.
    private static readonly TimeSpan PartialFlushDebounceInterval = TimeSpan.FromMilliseconds(100);

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

        return SendMessageCoreAsync(request, cancellationToken);
    }

    private async IAsyncEnumerable<ChatStreamEvent> SendMessageCoreAsync(NodeChatStreamRequest request,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        // Reject sends to a remote-origin (view-only) conversation before any persistence happens. The guard is
        // authoritative; throwing here propagates to the hub caller.
        await mutationGuard.EnsureMutableAsync(request.ConversationId, cancellationToken).ConfigureAwait(false);

        var conversation = await persistence.GetConversationAsync(request.ConversationId, cancellationToken).ConfigureAwait(false)
                           ?? throw new InvalidOperationException("The node chat conversation was not found.");

        // A selection map on the request is the authoritative, just-clicked path: persist it before building
        // context so the stored selection and the context agree. With no map on the request, fall back to the
        // selection already persisted on the conversation (loaded into the DTO).
        var selectedPath = request.SelectedPath is not null
            ? await persistence.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest(request.ConversationId, request.SelectedPath, NowUnixMilliseconds()), cancellationToken).ConfigureAwait(false)
            : conversation.SelectedPath;

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

        var assistantPlaceholder = await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(request.ConversationId,
                assistantMessageId,
                requestId,
                NowUnixMilliseconds(),
                // Stamp the placeholder with the model that will actually run (the agent pin when honored, the user's
                // explicit dropdown pick when it suppressed the pin, else the local-default) — never the raw request
                // model — so the attribution shown in the UI matches the run from the first pending frame.
                resolution.EffectiveModel,
                AgentDefinitionId: resolution.Resolved?.AgentDefinitionId,
                AgentName: resolution.Resolved?.AgentName,
                // Persist the effort that actually drives this turn — an agent's pinned effort wins over the request's
                // selection (same precedence as the runtime package built below). Survives reload off the metadata blob.
                ReasoningEffort: resolution.Resolved?.ReasoningEffort ?? request.ReasoningEffort),
            cancellationToken).ConfigureAwait(false);
        yield return ToMessageEvent(ChatStreamEventTypes.AssistantPending, correlation, assistantPlaceholder, sequence.Next());

        // The turn is Queued until the collision-queue lease is acquired in RunInvocationAsync; it transitions to
        // Streaming only when the invocation actually starts. This keeps a turn waiting behind another invocation
        // visibly "queued" rather than prematurely "streaming".
        var queuedMessage = await persistence.MarkAssistantQueuedAsync(correlation, NowUnixMilliseconds(), cancellationToken).ConfigureAwait(false);
        yield return ToMessageEvent(ChatStreamEventTypes.AssistantQueued, correlation, queuedMessage, sequence.Next());

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
        var eventChannel = Channel.CreateUnbounded<ChatStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            // Three producers write this channel concurrently: the delta/terminal emits in PumpInvocationStatesAsync
            // (the invocation-state pump), the streaming-transition emit in RunInvocationAsync (the run-transition),
            // and the tool-call lifecycle emits in OnToolCallLifecycleChanged. SingleWriter must be false.
            SingleWriter = false
        });

        void OnInvocationStateChanged(object? _, InvocationStateChangedEventArgs args)
        {
            if (args.State.InvocationId == requestId)
            {
                stateChannel.Writer.TryWrite(args.State);
            }
        }

        // Accumulates the ordered reasoning/tool interleave so the terminal persist can write parts[] (the reload
        // render source). Fed by BOTH producers: the tool handler below and the reasoning deltas in the pump loop.
        var parts = new NodeChatPartAccumulator();

        void OnToolCallLifecycleChanged(object? _, ToolCallLifecycleChangedEventArgs args)
        {
            if (args.Payload.InvocationId == requestId)
            {
                var toolSequence = sequence.Next();
                AccumulateToolPart(parts, args.Payload, toolSequence);
                eventChannel.Writer.TryWrite(ToToolCallEvent(correlation, args.Payload, toolSequence));
            }
        }

        eventDispatcher.InvocationStateChanged += OnInvocationStateChanged;
        eventDispatcher.ToolCallLifecycleChanged += OnToolCallLifecycleChanged;

        // The active-model precedence, the effective-agent resolution, and the orchestration spec were all computed up
        // front (ResolveTurnAsync) so the placeholder could be stamped with the resolved agent's attribution; reuse
        // those results here unchanged.
        var activeModel = resolution.ActiveModel;
        var resolved = resolution.Resolved;
        var orchestration = resolution.Orchestration;

        // Tools are offered to the loopback agent only when the client asked for them AND the node has the agent
        // tool engine enabled AND the active model advertises the Ollama tools capability. When offered, the catalog's
        // local tools travel in the runtime package as the offer list; the invocation factory resolves the matching
        // executables from the registry by name. A bound definition narrows that offer to its allowed set (and the
        // resolver already withheld the offer for a non-tools model); an unbound conversation uses the full offer.
        var enableTools = await runtimeSettings.GetEnableToolsAsync(cancellationToken).ConfigureAwait(false);
        var offerTools = request.UseLocalTools && enableTools && resolution.SupportsTools;
        var allowedTools = offerTools
            ? resolved?.AllowedTools ?? localToolOfferProvider.GetOfferedTools(activeModel)
            : null;

        // Agent mode: when the selected agent can read files through the AgentHome sandbox (its offer includes the
        // read-only coder tools or run_in_agent_home), re-stage the sandbox with THIS conversation's uploaded attachments
        // BEFORE building the turn context, so list_files/read_file/search_text see them under attachments/. The stager
        // returns the exact staged paths (empty when Agent Mode is off or there are no extracted files).
        var isAgentHomeTurn = offerTools && OffersAgentHomeTools(allowedTools);
        var stagedAttachmentPaths = isAgentHomeTurn
            ? await PrepareConversationAttachmentsSafelyAsync(request.ConversationId, runCancellation.Token).ConfigureAwait(false)
            : [];

        // The synthetic prepended context differs by mode: plain chat inlines the extracted text directly; agent mode
        // injects only a short pointer naming the staged files (the agent reads their content through its tools, so the
        // text is not double-fed). The pointer is what stops a weak model from guessing a wrong file name.
        var attachmentContext = offerTools
            ? BuildAgentAttachmentHint(stagedAttachmentPaths)
            : await BuildAttachmentContextMessageAsync(request.ConversationId, request.AttachmentFileIds, cancellationToken).ConfigureAwait(false);

        var package = runtimePackageBuilder.Build(new LocalChatRuntimePackageRequest(requestId,
            request.ConversationId,
            resolved?.ResolvedSystemPrompt ?? LoadResolvedSystemPrompt(localChatOptions.Value),
            BuildConversationContext(conversation, userMessage, selectedPath, attachmentContext),
            resolution.EffectiveModel,
            resolved?.AgentDefinitionVersion ?? AgentDefinitionVersion,
            LocalChatLoopbackDefaults.ClientNodeId,
            allowedTools,
            RequestedCapabilities: [LocalChatLoopbackDefaults.RequestedCapability],
            ReasoningEffort: resolved?.ReasoningEffort ?? request.ReasoningEffort,
            OrchestrationSpec: orchestration?.Spec,
            SupportsThinking: resolution.SupportsThinking,
            SamplingOptions: request.SamplingOptions,
            Skills: resolved?.Skills));

        // Post-run adaptive-memory hook: fired once when the pump persists a Completed/Failed terminal, but ONLY when the
        // resolved agent has the playbook enabled AND opts into extraction. Retrieval/injection rides PlaybookEnabled
        // alone (already baked into the resolved prompt above); MemoryExtractionEnabled additionally gates whether this
        // run mines NEW candidates, so a retrieval-only agent (extraction off) still uses its memory but learns nothing
        // new — and skips the extraction round-trip entirely. Built here (not inside the pump) so it closes over the run
        // context the stream service already holds (resolved agent, conversation temp flag, user turns, package config
        // hash); the pump stays content-free. The dispatch is fire-and-forget (its own scope + fresh CT) so it never
        // delays the SSE.
        var onTerminal = resolution.Resolved is { PlaybookEnabled: true, MemoryExtractionEnabled: true } memoryAgent
            ? BuildMemoryExtractionHook(memoryAgent, conversation, userMessage, selectedPath, package, resolution.EffectiveModel)
            : null;

        var pumpTask = PumpInvocationStatesAsync(stateChannel.Reader,
            eventChannel.Writer,
            correlation,
            // Stamp the FINAL persisted assistant-message model from the effective model (the pump terminalizes from
            // this requestedModel), so the stored attribution reflects the model that actually ran, not request.Model.
            resolution.EffectiveModel,
            sequence,
            parts,
            onTerminal,
            runCancellation.Token);
        var runTask = RunInvocationAsync(package,
            assistantMessageId,
            stateChannel.Writer,
            eventChannel.Writer,
            correlation,
            requestId,
            sequence,
            resolution.RequiresInstalledChatModel,
            runCancellation.Token);

        try
        {
            // Forward persisted events to the client. The client cancellationToken stops THIS loop only (e.g. the
            // browser/SignalR stream unsubscribed or disconnected). It does not cancel the run or the pump: those
            // keep going on runCancellation.Token so the runner reaches its real terminal and the pump persists it.
            await foreach (var streamEvent in eventChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return streamEvent;
            }
        }
        finally
        {
            // Do NOT cancel runCancellation here on a client disconnect. Let runTask and pumpTask drain to the
            // runner's true terminal (Completed/Failed/Cancelled) so persistence follows the runner's lifecycle,
            // not the client connection's. A genuine user cancel already tripped runCancellation via the registry.
            //
            // DECISION (handoff #2): because the run keeps going, RunInvocationAsync also holds the collision-slot
            // lease until the runner finishes, so a disconnected mid-run turn keeps the slot alive. Accepted as-is
            // for single-user local — at most one queued turn waits, then both persist correctly. If contended
            // multi-session local ever matters, add an explicit disconnect->cancel path distinct from this SSE
            // unsubscribe; do NOT free the slot from here, which would resurrect the interrupted-terminal bug.
            //
            // IMPORTANT: unsubscribe AFTER awaiting runTask/pumpTask, not before. The runner may fire
            // InvocationStateChanged (the Completed terminal) after the SSE loop exits. If we unsubscribe first,
            // the terminal state never reaches the stateChannel, the pump ends without a terminal, and the message
            // is falsely persisted as interrupted.
            try
            {
                await Task.WhenAll(runTask, pumpTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The terminal cancelled/interrupted event is persisted by the pump.
            }
            finally
            {
                eventDispatcher.InvocationStateChanged -= OnInvocationStateChanged;
                eventDispatcher.ToolCallLifecycleChanged -= OnToolCallLifecycleChanged;
            }
        }
    }

    private async Task RunInvocationAsync(RuntimePackage package,
        Guid messageId,
        ChannelWriter<InvocationState> stateWriter,
        ChannelWriter<ChatStreamEvent> eventWriter,
        NodeChatMessageCorrelation correlation,
        Guid requestId,
        NodeChatStreamSequence sequence,
        bool requiresInstalledChatModel,
        CancellationToken cancellationToken)
    {
        // Queue behind any in-flight invocation (local or platform) before assigning, rather than failing the
        // turn. The lease holds the shared slot for this run; cancelling while still queued aborts the wait and
        // terminalizes the turn as cancelled below.
        IAsyncDisposable? lease = null;

        try
        {
            lease = await eventDispatcher.ReportInvocationAssignedAsync(package, cancellationToken).ConfigureAwait(false);

            // The lease is held => the invocation is actually starting. Transition Queued -> Streaming and emit
            // the streaming event so the client leaves the queued state.
            var streamingMessage = await persistence.MarkAssistantStreamingAsync(correlation, NowUnixMilliseconds(), cancellationToken).ConfigureAwait(false);
            await eventWriter.WriteAsync(ToMessageEvent(ChatStreamEventTypes.AssistantStreaming, correlation, streamingMessage, sequence.Next()), cancellationToken).ConfigureAwait(false);

            // A "Local runtime default" send that resolved no installed GGUF chat model fails BEFORE any provider
            // invocation with a dedicated category, so the client sees an actionable "pull a model" terminal rather
            // than the stale-id "Provider unreachable.".
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
                "Invocation timed out or was cancelled",
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

    private async Task PumpInvocationStatesAsync(ChannelReader<InvocationState> stateReader,
        ChannelWriter<ChatStreamEvent> eventWriter,
        NodeChatMessageCorrelation correlation,
        string? requestedModel,
        NodeChatStreamSequence sequence,
        NodeChatPartAccumulator parts,
        Action<InvocationState, NodeChatPumpTerminalResult>? onTerminal,
        CancellationToken cancellationToken)
    {
        // The shared pump (invocationPump) owns all persistence; this front door only fans the persisted
        // results out as SSE ChatStreamEvents for the local response. The sequence counter is shared with the
        // streaming-transition event emitted from RunInvocationAsync so all events stay monotonically ordered.
        var cursor = NodeChatPumpCursor.Empty;
        var terminalPersisted = false;
        var hasFlushedPartial = false;
        var lastPartialFlushTimestamp = 0L;
        // The freshest content-bearing snapshot the debounce deferred without persisting. Retained so a graceful
        // end-of-stream that never delivers a terminal still writes the tail rather than a stale cursor.
        InvocationState? pendingPartialState = null;

        // Persists one snapshot's content/reasoning delta and fans it out as an AssistantDelta. Returns whether a
        // delta advanced. Each InvocationState carries the FULL accumulated content/reasoning, so flushing only the
        // latest snapshot after coalescing still captures everything between the cursor and that snapshot.
        async Task<bool> PersistPartialAsync(InvocationState snapshotToFlush)
        {
            var flush = await invocationPump.FlushDeltaAsync(correlation, snapshotToFlush, cursor, cancellationToken).ConfigureAwait(false);
            cursor = flush.Cursor;

            if (flush.Persisted is null)
            {
                return false;
            }

            lastPartialFlushTimestamp = timeProvider.GetTimestamp();
            hasFlushedPartial = true;
            var deltaSequence = sequence.Next();
            // The reasoning delta and its SSE event share this flush-time sequence. Tool parts stamp their own
            // sequence synchronously when the tool lifecycle fires on the separate event channel, so debouncing the
            // reasoning flush can push a reasoning segment behind a tool part that streamed just after it — widening
            // the pre-existing reasoning<->tool interleave skew to at most one debounce window. This is display-only:
            // the reasoning text and the tool parts are all retained; only their relative order at a tool boundary can
            // shift within that window.
            parts.AppendReasoning(flush.ReasoningDelta, deltaSequence);

            await eventWriter.WriteAsync(ToMessageEvent(ChatStreamEventTypes.AssistantDelta,
                    correlation,
                    flush.Persisted,
                    deltaSequence,
                    flush.ContentDelta,
                    flush.ReasoningDelta),
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        try
        {
            await foreach (var state in stateReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Coalesce a burst: when the runner produces states faster than we persist, they queue on the
                // channel. Drain the backlog and keep only the newest snapshot (never draining past a terminal), so
                // a burst of per-token states collapses into a single flush without losing content.
                var latest = state;
                while (!NodeChatInvocationPump.IsTerminal(latest.Status) && stateReader.TryRead(out var queued))
                {
                    latest = queued;
                }

                var isTerminal = NodeChatInvocationPump.IsTerminal(latest.Status);

                // Terminal/error flushes immediately (the final delta + reasoning tail must not be delayed); the first
                // partial also flushes immediately so the first token is visible without waiting a window. Between
                // windows the snapshot is deferred so it is not lost if the stream ends without a terminal.
                if (isTerminal
                    || !hasFlushedPartial
                    || timeProvider.GetElapsedTime(lastPartialFlushTimestamp) >= PartialFlushDebounceInterval)
                {
                    pendingPartialState = null;
                    await PersistPartialAsync(latest).ConfigureAwait(false);
                }
                else
                {
                    pendingPartialState = latest;
                }

                if (isTerminal)
                {
                    // An empty snapshot (a plain-text turn with no reasoning/tools) is passed as null so the persisted
                    // parts are left untouched rather than overwritten with an empty interleave.
                    var snapshot = parts.HasParts ? parts.Snapshot() : null;
                    var terminal = await invocationPump.TerminalizeAsync(correlation, latest, requestedModel, snapshot).ConfigureAwait(false);
                    terminalPersisted = true;

                    // Post-run adaptive memory: hand the just-persisted terminal to the (background, fire-and-forget)
                    // extraction hook before the SSE write so the run context is captured immediately. The hook never
                    // blocks or throws into the pump (it only schedules work on its own scope).
                    onTerminal?.Invoke(latest, terminal);

                    await eventWriter.WriteAsync(ToMessageEvent(terminal.EventType,
                            correlation,
                            terminal.Persisted,
                            sequence.Next(),
                            inputTokens: latest.InputTokens,
                            outputTokens: latest.OutputTokens,
                            totalTokens: latest.TotalTokens,
                            reasoningTokens: latest.ReasoningTokens),
                        CancellationToken.None).ConfigureAwait(false);
                    break;
                }
            }

            if (!terminalPersisted)
            {
                // Flush any tail the debounce deferred so the interrupted terminal is written from the freshest
                // content rather than a stale cursor.
                if (pendingPartialState is not null)
                {
                    await PersistPartialAsync(pendingPartialState).ConfigureAwait(false);
                }

                await TerminalizeInterruptedStreamAsync(eventWriter,
                    correlation,
                    sequence.Next(),
                    cursor,
                    cancellationToken.IsCancellationRequested).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !terminalPersisted)
        {
            // Deliberate trade-off: the cancelled message is terminalized from the last-persisted cursor, NOT from a
            // debounce-deferred pendingPartialState, so a user cancel can drop up to one debounce window of tail
            // tokens off the cancelled turn. Re-flushing here is not an option — the cancellation token is already
            // tripped, so FlushDeltaAsync/WriteAsync would just throw again. Accepted because a cancelled turn is
            // discarded output anyway, and this stays within the same one-window crash-consistency bound.
            await TerminalizeInterruptedStreamAsync(eventWriter,
                correlation,
                sequence.Next(),
                cursor,
                wasCancelled: true).ConfigureAwait(false);
        }
        finally
        {
            eventWriter.TryComplete();
        }
    }

    private async Task TerminalizeInterruptedStreamAsync(ChannelWriter<ChatStreamEvent> eventWriter,
        NodeChatMessageCorrelation correlation,
        long sequence,
        NodeChatPumpCursor cursor,
        bool wasCancelled)
    {
        var terminal = await invocationPump.TerminalizeInterruptedAsync(correlation, cursor, wasCancelled).ConfigureAwait(false);

        await eventWriter.WriteAsync(ToMessageEvent(terminal.EventType, correlation, terminal.Persisted, sequence), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    ///     Builds the post-run memory-extraction hook fired by the pump on a Completed/Failed terminal. It closes over the
    ///     resolved agent (provenance + config-hash inputs), the loaded conversation (temp-chat flag + prior user turns),
    ///     the just-sent user turn, and the runtime package (config hash). On invoke it assembles the metadata-only
    ///     execution-log telemetry plus the content-bearing run input and hands both to the background dispatcher; the
    ///     dispatcher owns scope/CT/error isolation, so this delegate never blocks or throws into the pump.
    /// </summary>
    private Action<InvocationState, NodeChatPumpTerminalResult> BuildMemoryExtractionHook(ResolvedAgentRuntime resolved,
        NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto userMessage,
        IReadOnlyDictionary<Guid, Guid>? selectedPath,
        RuntimePackage package,
        string? requestedModel)
    {
        return (state, terminal) =>
        {
            // Only Completed/Failed terminals carry a real run to learn from; a Cancelled/Interrupted terminal is not a
            // finished answer, so skip it (no exec-log either — nothing meaningful ran).
            var failed = state.Status == InvocationStatus.Failed;
            if (state.Status != InvocationStatus.Completed && !failed)
            {
                return;
            }

            var modelName = state.ModelUsed ?? requestedModel ?? package.ModelProfile ?? string.Empty;
            var telemetry = new MemoryExtractionDispatchContext(resolved.AgentDefinitionId,
                conversation.ConversationId,
                terminal.Persisted.MessageId,
                modelName,
                package.ConfigHash,
                state.GenerationDurationMs ?? 0,
                !failed,
                state.InputTokens,
                state.OutputTokens,
                // Exception TYPE NAME only when present — never the sanitized message text. FailureCategory is the only
                // type-shaped signal at this seam; the sanitized state.Error string is NOT logged.
                failed ? state.FailureCategory?.ToString() : null);

            var run = new MemoryExtractionRunInput(resolved.AgentDefinitionId,
                conversation.ConversationId,
                terminal.Persisted.MessageId,
                CollectUserTurns(conversation, userMessage, selectedPath),
                state.StreamedContent,
                failed,
                state.Error,
                conversation.MemoryExcluded);

            memoryExtractionDispatcher.Dispatch(telemetry, run);
        };
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
        var selected = SelectedPathResolver.Resolve(conversation.Messages, selectedPath);

        return selected
               .Where(static message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                                        && !string.IsNullOrWhiteSpace(message.Content)
                                        && string.Equals(message.Status, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal))
               .Concat([userMessage])
               .OrderBy(static message => message.Sequence)
               .Select(static message => new MemoryExtractionTurn(message.Content))
               .ToArray();
    }

    private static IReadOnlyList<ConversationMessageDto> BuildConversationContext(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto userMessage,
        IReadOnlyDictionary<Guid, Guid>? selectedPath,
        ConversationMessageDto? attachmentContext)
    {
        // Collapse variant siblings to the selected path FIRST (one variant per group, newest by default), then
        // apply the existing content/status filters. Without this every regenerated sibling would be sent as
        // context; the resolver keeps only the chosen branch.
        var selected = SelectedPathResolver.Resolve(conversation.Messages, selectedPath);

        // The attachment context applies to plain chat only and is prepended so the model reads the file content
        // before the conversation history. When present it takes the first slot and the history shifts down by one.
        var historyOffset = attachmentContext is null ? 0 : 1;

        var history = selected
                      .Where(static message => !string.IsNullOrWhiteSpace(message.Content)
                                               && string.Equals(message.Status, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal))
                      .Concat([userMessage])
                      .OrderBy(static message => message.Sequence)
                      .Select((message, index) => new ConversationMessageDto
                      {
                          Id = message.MessageId,
                          Role = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? MessageRole.Assistant : MessageRole.User,
                          Content = message.Content,
                          Thinking = message.Reasoning,
                          ModelUsed = message.Model,
                          SortOrder = index + historyOffset
                      });

        return attachmentContext is null ? history.ToList() : history.Prepend(attachmentContext).ToList();
    }

    // Builds the synthetic plain-chat context message from the conversation's uploaded attachments named in the send.
    // Only Extracted files contribute; the combined text is capped to the configured MaxInlinedAttachmentChars budget
    // with a truncation notice. Returns null when there is nothing to inline (the common no-attachment path
    // short-circuits before any store call).
    private async Task<ConversationMessageDto?> BuildAttachmentContextMessageAsync(Guid conversationId,
        IReadOnlyList<Guid>? attachmentFileIds,
        CancellationToken cancellationToken)
    {
        if (attachmentFileIds is null || attachmentFileIds.Count == 0)
        {
            return null;
        }

        var requested = attachmentFileIds.ToHashSet();
        var available = await uploadedFileStore.ListAsync(conversationId, cancellationToken).ConfigureAwait(false);
        var attachments = available
                          .Where(file => requested.Contains(file.FileId) && file.ExtractionStatus == DocumentExtractionStatus.Extracted)
                          .ToList();

        if (attachments.Count == 0)
        {
            return null;
        }

        var parts = new List<AttachmentTextPart>(attachments.Count);
        foreach (var attachment in attachments)
        {
            var markdown = await uploadedFileStore.ReadExtractedMarkdownAsync(conversationId, attachment.FileId, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(markdown))
            {
                parts.Add(new AttachmentTextPart(attachment.OriginalFileName, markdown));
            }
        }

        var content = ConversationAttachmentContextComposer.Compose(parts, localChatOptions.Value.MaxInlinedAttachmentChars);
        if (content is null)
        {
            return null;
        }

        return new ConversationMessageDto
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = content,
            SortOrder = 0
        };
    }

    private static bool OffersAgentHomeTools(IReadOnlyList<AllowedToolDto>? allowedTools)
    {
        return allowedTools is not null && allowedTools.Any(tool => AgentHomeCapableToolNames.Contains(tool.Name));
    }

    // Best-effort attachment staging: re-stages the conversation's uploaded attachments into the node sandbox so the
    // agent's file tools can read them, returning the workspace-relative staged paths (empty on failure or no-op). A
    // failure leaves the agent without the staged workspace (its file tools report "no workspace") but must never fail
    // the chat turn — a genuine user cancel is handled by the run path downstream.
    private async Task<IReadOnlyList<string>> PrepareConversationAttachmentsSafelyAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        try
        {
            return await conversationSandboxStager.PrepareConversationAttachmentsAsync(conversationId, cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "AgentHome attachment staging for conversation {ConversationId} failed; the agent will run without staged attachments.",
                conversationId);
            return [];
        }
    }

    // Builds the agent-mode pointer message naming the staged attachment paths, so a weak model reads the exact staged
    // file (whole-file, no guessed name) through its tools. Returns null when nothing was staged, leaving the turn
    // context byte-identical to the no-attachment agent path. The file CONTENT is never inlined here (the agent reads it
    // via read_file) — only the pointer travels in context.
    private static ConversationMessageDto? BuildAgentAttachmentHint(IReadOnlyList<string> stagedAttachmentPaths)
    {
        if (stagedAttachmentPaths.Count == 0)
        {
            return null;
        }

        var fileLines = string.Join('\n', stagedAttachmentPaths.Select(static path => "- " + path));
        var content =
            "The files the user uploaded to this conversation have been staged into your read-only workspace. Before "
            + "answering, read them with your file tools — call read_file with the exact path below and no startLine/endLine "
            + "so you get the whole file. Do not guess other file names.\nStaged files:\n"
            + fileLines;

        return new ConversationMessageDto
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = content,
            SortOrder = 0
        };
    }

    private static string LoadResolvedSystemPrompt(LocalChatAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.InstructionsResource))
        {
            throw new ArgumentException("Instructions resource must be provided.", nameof(options));
        }

        var assembly = typeof(LocalChatAgentOptions).Assembly;
        using var stream = assembly.GetManifestResourceStream(options.InstructionsResource)
                           ?? throw new InvalidOperationException($"Embedded instructions resource '{options.InstructionsResource}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    ///     Computes the offer-time active model and resolves the effective per-turn agent (definition + orchestration)
    ///     up front so the assistant placeholder can be stamped with the resolved agent's attribution. The effective-agent
    ///     precedence is
    ///     <c>
    ///         request.AgentDefinitionId ?? conversation.AgentDefinitionId ?? (memoized) Default Assistant
    ///         id
    ///     </c>
    ///     ; resolving the Default Assistant on a cold conversation must NOT throw — a missing seed yields a null id,
    ///     the resolver returns null, and the caller keeps the embedded default persona + full offer + the client
    ///     "Default Assistant" label.
    /// </summary>
    private async Task<ResolvedTurn> ResolveTurnAsync(NodeChatStreamRequest request,
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

        // Resolve the active model's advertised Ollama capabilities ONCE so the think field and the tool offer are both
        // gated by what the model can actually do. A non-thinking model returns HTTP 400 for any think value; a
        // non-tools model cannot drive tool calls. Unknown/offline capabilities resolve to NOT-capable (the safe
        // default) so a plain chat still works without tripping the 400. Cache hit issues no /api/show call.
        var (supportsThinking, supportsTools) = await ResolveModelCapabilitiesAsync(activeModel, cancellationToken).ConfigureAwait(false);

        // Resolve the effective agent definition. A null result — no effective id (the seed is missing), or an id whose
        // definition was deleted — keeps the default persona: the embedded system prompt, the full capability-gated
        // offer, and agent version 1. When resolved, the definition supplies the system prompt, the tool offer (full for
        // the Default Assistant, intersected otherwise), the pinned model profile, the reasoning effort, the version
        // that feeds the config hash, AND the attribution snapshot (id + display name). The just-sent user turn is the
        // relevance-retrieval query (inert below the threshold / unbound, so the prompt stays byte-identical).
        var resolved = await agentDefinitionResolver.ResolveAsync(effectiveAgentId, activeModel, trimmedContent, supportsTools, honorModelProfile: !userPickedConcreteModel, cancellationToken)
                                                    .ConfigureAwait(false);

        // When the effective definition is a tool-capable orchestrator, resolve a compiled orchestration spec to carry
        // on the package — the runner branches to the handoff workflow. A null result (not an orchestrator, an
        // empty/invalid topology, an incapable model, or too few capable participants) leaves the package single-agent,
        // keeping the unbound/single-agent path byte-identical. The same user turn drives per-participant retrieval.
        var orchestration = await ResolveOrchestrationAsync(effectiveAgentId, activeModel, trimmedContent, supportsTools, cancellationToken).ConfigureAwait(false);

        // The single source of truth for the model that actually runs this turn: the resolved pin when honored (the
        // resolver returned null for ModelProfile when the user's explicit pick suppressed it), otherwise the active
        // model. Both the runtime package AND the persisted assistant-message attribution are stamped from this so the
        // label can never disagree with what ran.
        var effectiveModel = resolved?.ModelProfile ?? activeModel;

        return new ResolvedTurn(activeModel, effectiveModel, resolved, orchestration, supportsThinking, supportsTools, requiresInstalledChatModel);
    }

    /// <summary>
    ///     Resolves the active model's advertised <c>thinking</c>/<c>tools</c> capabilities via the shared classification
    ///     service (cache-first; no <c>/api/show</c> call on a cache hit). A null/blank model or any detection miss
    ///     resolves to NOT-capable for both — the safe default that omits the think field (avoiding the Ollama 400) and
    ///     withholds the tool offer while still allowing a plain chat.
    /// </summary>
    private async Task<(bool SupportsThinking, bool SupportsTools)> ResolveModelCapabilitiesAsync(string? activeModel, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(activeModel))
        {
            return (false, false);
        }

        // A Codex cloud model is NOT an Ollama model: classifying it against the local runtime's /api/show would
        // mis-detect it (the runtime has never seen it). Use the Codex provider's declared capability matrix
        // instead. Codex models reason by default, so thinking is on; tool calling tracks the V0 matrix, which now
        // ENABLES tools for all Codex ids (de-risk verified — encrypted reasoning round-trips through the stateless
        // tool loop). Cloud providers ignore the unknown think property, so the reasoning gate stays inert on the wire.
        if (CodexModelCatalog.IsCodexModel(activeModel))
        {
            return (SupportsThinking: true, CodexProviderCapabilities.V0.SupportsToolCalling);
        }

        // An Azure Foundry deployment id is NOT an Ollama model either: probing /api/show for it would 500/stall.
        // When the active model matches a stored Azure deployment name, advertise the Azure provider's declared
        // capability matrix instead of an Ollama classification, so an Azure id never falls through to the local probe.
        if (await IsAzureFoundryModelAsync(activeModel, cancellationToken).ConfigureAwait(false))
        {
            return (SupportsThinking: false, SupportsTools: AzureFoundryProviderCapabilities.V0.SupportsToolCalling);
        }

        // /api/show classification only makes sense for an Ollama-routed model. A llama.cpp (GGUF) model has no Ollama
        // entry, so probing the local runtime would always fail — and in desktop mode there is no Ollama daemon at all,
        // so the probe would stall (up to the connect timeout) on every send. Instead, read the GGUF's capabilities
        // detected offline from its chat template (cheap, cached per file — no Ollama probe, no network), matching the
        // model-list classification (LocalModelsMapper.ToLlamaCppModelResponses). Skip the doomed probe for any
        // non-Ollama provider; for a llama.cpp model the GGUF detection supplies thinking/tools, otherwise the safe
        // default applies.
        var providerName = await localModelProviderResolver
                                 .ResolveProviderNameForModelAsync(activeModel, cancellationToken)
                                 .ConfigureAwait(false);
        if (!string.Equals(providerName, OllamaLocalModelProvider.OllamaProviderName, StringComparison.OrdinalIgnoreCase))
        {
            var ggufCapabilities = await ggufModelCapabilityResolver
                                         .TryResolveAsync(activeModel, cancellationToken)
                                         .ConfigureAwait(false);
            return ggufCapabilities is { } caps
                ? (caps.SupportsThinking, caps.SupportsTools)
                : (SupportsThinking: false, SupportsTools: false);
        }

        var classifications = await modelClassificationService
                                    .ClassifyAsync([(activeModel, null)], cancellationToken)
                                    .ConfigureAwait(false);
        if (!classifications.TryGetValue(activeModel, out var classification))
        {
            return (false, false);
        }

        return (ModelKindDetector.SupportsThinking(classification.Capabilities),
            ModelKindDetector.SupportsTools(classification.Capabilities));
    }

    /// <summary>
    ///     True when <paramref name="activeModel" /> matches one of the stored Azure Foundry connection's deployment
    ///     names (ordinal, case-insensitive). A best-effort read: any failure resolving the encrypted config is treated
    ///     as "not Azure" so the capability gate falls through to its existing (safe) classification rather than failing
    ///     the send.
    /// </summary>
    private async Task<bool> IsAzureFoundryModelAsync(string activeModel, CancellationToken cancellationToken)
    {
        try
        {
            var config = await cloudCredentialStore.LoadConfigAsync(cancellationToken).ConfigureAwait(false);
            var connection = config?.AzureFoundry;
            return connection is { Models.Count: > 0 }
                   && connection.Models.Any(model => string.Equals(model.DeploymentName, activeModel, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Azure Foundry deployment match could not be resolved for '{Model}'.", activeModel);
            return false;
        }
    }

    /// <summary>
    ///     Resolves a compiled orchestration spec for a bound orchestrator definition (orchestration), or <c>null</c> to run
    ///     the turn single-agent. Only a bound conversation triggers the extra record fetch; an unbound conversation or
    ///     a non-orchestrator definition returns <c>null</c> without resolving, so the single-agent path is byte-identical.
    /// </summary>
    private async Task<ResolvedOrchestration?> ResolveOrchestrationAsync(Guid? agentDefinitionId,
        string? activeModel,
        string? retrievalQuery,
        bool supportsTools,
        CancellationToken cancellationToken)
    {
        if (agentDefinitionId is not { } definitionId)
        {
            return null;
        }

        var definition = await agentDefinitionStore.GetByIdAsync(definitionId, cancellationToken).ConfigureAwait(false);
        if (definition is null || definition.Kind != AgentDefinitionKind.Orchestrator)
        {
            return null;
        }

        return await orchestrationResolver.ResolveAsync(definition, activeModel, retrievalQuery, supportsTools, cancellationToken).ConfigureAwait(false);
    }

    private ChatStreamEvent ToMessageEvent(string type,
        NodeChatMessageCorrelation correlation,
        NodeChatPersistedMessageDto message,
        long sequence,
        string? delta = null,
        string? reasoningDelta = null,
        int? inputTokens = null,
        int? outputTokens = null,
        int? totalTokens = null,
        int? reasoningTokens = null)
    {
        return new ChatStreamEvent(type,
            correlation.ConversationId,
            correlation.MessageId,
            correlation.RequestId,
            message.Status,
            sequence,
            NowUnixMilliseconds(),
            delta,
            reasoningDelta,
            message.Content,
            message.Reasoning,
            message.Error,
            message.Model,
            inputTokens ?? message.InputCount,
            outputTokens ?? message.OutputCount,
            totalTokens ?? message.TotalCount,
            reasoningTokens ?? message.ReasoningCount);
    }

    private static void AccumulateToolPart(NodeChatPartAccumulator parts, ToolCallLifecyclePayload payload, long sequence)
    {
        if (payload.Phase == ToolCallLifecyclePhase.Requested)
        {
            parts.AppendToolRequested(payload.ToolCallId, payload.ToolName, payload.Arguments, payload.RequiresApproval, sequence);
            return;
        }

        parts.CompleteToolCall(payload.ToolCallId, payload.ToolName, payload.Result, payload.IsError, sequence);
    }

    private ChatStreamEvent ToToolCallEvent(NodeChatMessageCorrelation correlation,
        ToolCallLifecyclePayload payload,
        long sequence)
    {
        var type = payload.Phase == ToolCallLifecyclePhase.Requested
            ? ChatStreamEventTypes.ToolCallRequested
            : ChatStreamEventTypes.ToolCallCompleted;

        return new ChatStreamEvent(type,
            correlation.ConversationId,
            correlation.MessageId,
            correlation.RequestId,
            NodeChatMessageStatusValues.Streaming,
            sequence,
            NowUnixMilliseconds(),
            ToolCallId: payload.ToolCallId,
            ToolName: payload.ToolName,
            Arguments: payload.Phase == ToolCallLifecyclePhase.Requested ? payload.Arguments : null,
            RequiresApproval: payload.Phase == ToolCallLifecyclePhase.Requested ? payload.RequiresApproval : null,
            Result: payload.Phase == ToolCallLifecyclePhase.Completed ? payload.Result : null,
            IsError: payload.Phase == ToolCallLifecyclePhase.Completed ? payload.IsError : null);
    }

    private long NowUnixMilliseconds()
    {
        return timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }

    /// <summary>The up-front per-turn resolution shared by placeholder stamping and runtime-package construction.</summary>
    private sealed record ResolvedTurn(
        string? ActiveModel,
        string? EffectiveModel,
        ResolvedAgentRuntime? Resolved,
        ResolvedOrchestration? Orchestration,
        bool SupportsThinking,
        bool SupportsTools,
        bool RequiresInstalledChatModel);
}
