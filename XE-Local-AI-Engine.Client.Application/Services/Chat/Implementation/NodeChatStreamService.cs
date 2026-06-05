namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

public sealed class NodeChatStreamService(
    INodeChatPersistenceService persistence,
    INodeChatInvocationPump invocationPump,
    INodeChatMutationGuard mutationGuard,
    ILocalChatRuntimePackageBuilder runtimePackageBuilder,
    IInvocationRunner invocationRunner,
    IWorkerEventDispatcher eventDispatcher,
    IOptions<LocalChatAgentOptions> localChatOptions,
    INodeChatStreamCancellationRegistry cancellationRegistry,
    ILocalToolOfferProvider localToolOfferProvider,
    IAgentDefinitionResolver agentDefinitionResolver,
    IAgentDefinitionStore agentDefinitionStore,
    IDefaultAgentProvider defaultAgentProvider,
    IOrchestrationResolver orchestrationResolver,
    INodeSettingsStore nodeSettingsStore,
    IModelClassificationService modelClassificationService,
    TimeProvider timeProvider,
    ILogger<NodeChatStreamService> logger) : INodeChatStreamService
{
    private const int AgentDefinitionVersion = 1;

    public IAsyncEnumerable<ChatStreamEvent> SendMessageAsync(NodeChatStreamRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Content);

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
                request.Model,
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
        var offerTools = request.UseLocalTools && localChatOptions.Value.EnableTools && resolution.SupportsTools;
        var allowedTools = offerTools
            ? resolved?.AllowedTools ?? localToolOfferProvider.GetOfferedTools(activeModel)
            : null;

        var package = runtimePackageBuilder.Build(new LocalChatRuntimePackageRequest(requestId,
            request.ConversationId,
            resolved?.ResolvedSystemPrompt ?? LoadResolvedSystemPrompt(localChatOptions.Value),
            BuildConversationContext(conversation, userMessage, selectedPath),
            resolved?.ModelProfile ?? activeModel,
            resolved?.AgentDefinitionVersion ?? AgentDefinitionVersion,
            LocalChatLoopbackDefaults.ClientNodeId,
            AllowedTools: allowedTools,
            RequestedCapabilities: [LocalChatLoopbackDefaults.RequestedCapability],
            ReasoningEffort: resolved?.ReasoningEffort ?? request.ReasoningEffort,
            OrchestrationSpec: orchestration?.Spec,
            SupportsThinking: resolution.SupportsThinking,
            SamplingOptions: request.SamplingOptions));

        var pumpTask = PumpInvocationStatesAsync(stateChannel.Reader,
            eventChannel.Writer,
            correlation,
            request.Model,
            sequence,
            parts,
            runCancellation.Token);
        var runTask = RunInvocationAsync(package,
            assistantMessageId,
            stateChannel.Writer,
            eventChannel.Writer,
            correlation,
            requestId,
            sequence,
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

            using var context = InvocationExecutionContext.CreatePlain(package, messageId);
            await invocationRunner.RunAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await eventDispatcher.ReportInvocationFailedAsync(requestId,
                "Invocation timed out or was cancelled",
                FailureCategory.Cancelled).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        // The shared pump (invocationPump) owns all persistence; this front door only fans the persisted
        // results out as SSE ChatStreamEvents for the local response. The sequence counter is shared with the
        // streaming-transition event emitted from RunInvocationAsync so all events stay monotonically ordered.
        var cursor = NodeChatPumpCursor.Empty;
        var terminalPersisted = false;

        try
        {
            await foreach (var state in stateReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var flush = await invocationPump.FlushDeltaAsync(correlation, state, cursor, cancellationToken).ConfigureAwait(false);
                cursor = flush.Cursor;

                if (flush.Persisted is not null)
                {
                    var deltaSequence = sequence.Next();
                    // Feed the reasoning delta into the interleave under the SAME sequence as its SSE event so the
                    // accumulated reasoning segments order correctly against the concurrently-stamped tool parts.
                    parts.AppendReasoning(flush.ReasoningDelta, deltaSequence);

                    await eventWriter.WriteAsync(ToMessageEvent(ChatStreamEventTypes.AssistantDelta,
                            correlation,
                            flush.Persisted,
                            deltaSequence,
                            flush.ContentDelta,
                            flush.ReasoningDelta),
                        cancellationToken).ConfigureAwait(false);
                }

                if (NodeChatInvocationPump.IsTerminal(state.Status))
                {
                    // An empty snapshot (a plain-text turn with no reasoning/tools) is passed as null so the persisted
                    // parts are left untouched rather than overwritten with an empty interleave.
                    var snapshot = parts.HasParts ? parts.Snapshot() : null;
                    var terminal = await invocationPump.TerminalizeAsync(correlation, state, requestedModel, snapshot).ConfigureAwait(false);
                    terminalPersisted = true;

                    await eventWriter.WriteAsync(ToMessageEvent(terminal.EventType,
                            correlation,
                            terminal.Persisted,
                            sequence.Next(),
                            inputTokens: state.InputTokens,
                            outputTokens: state.OutputTokens,
                            totalTokens: state.TotalTokens,
                            reasoningTokens: state.ReasoningTokens),
                        CancellationToken.None).ConfigureAwait(false);
                    break;
                }
            }

            if (!terminalPersisted)
            {
                await TerminalizeInterruptedStreamAsync(eventWriter,
                    correlation,
                    sequence.Next(),
                    cursor,
                    cancellationToken.IsCancellationRequested).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !terminalPersisted)
        {
            await TerminalizeInterruptedStreamAsync(eventWriter,
                correlation,
                sequence.Next(),
                cursor,
                true).ConfigureAwait(false);
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

    private static IReadOnlyList<ConversationMessageDto> BuildConversationContext(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto userMessage,
        IReadOnlyDictionary<Guid, Guid>? selectedPath)
    {
        // Collapse variant siblings to the selected path FIRST (one variant per group, newest by default), then
        // apply the existing content/status filters. Without this every regenerated sibling would be sent as
        // context; the resolver keeps only the chosen branch.
        var selected = SelectedPathResolver.Resolve(conversation.Messages, selectedPath);

        var messages = selected
                       .Where(static message => !string.IsNullOrWhiteSpace(message.Content)
                                                && string.Equals(message.Status, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal))
                       .Concat([userMessage])
                       .OrderBy(static message => message.Sequence)
                       .Select(static (message, index) => new ConversationMessageDto
                       {
                           Id = message.MessageId,
                           Role = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? MessageRole.Assistant : MessageRole.User,
                           Content = message.Content,
                           Thinking = message.Reasoning,
                           ModelUsed = message.Model,
                           SortOrder = index
                       })
                       .ToList();

        return messages;
    }

    private static string LoadResolvedSystemPrompt(LocalChatAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.InstructionsResource);

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
        string activeModel;
        if (activeModelOverride is not null)
        {
            activeModel = activeModelOverride;
        }
        else
        {
            var nodeSettings = await nodeSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            activeModel = request.Model ?? nodeSettings.DefaultModelName ?? localChatOptions.Value.DefaultModel;
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
        var resolved = await agentDefinitionResolver.ResolveAsync(effectiveAgentId, activeModel, trimmedContent, supportsTools, cancellationToken).ConfigureAwait(false);

        // When the effective definition is a tool-capable orchestrator, resolve a compiled orchestration spec to carry
        // on the package — the runner branches to the handoff workflow. A null result (not an orchestrator, an
        // empty/invalid topology, an incapable model, or too few capable participants) leaves the package single-agent,
        // keeping the unbound/single-agent path byte-identical. The same user turn drives per-participant retrieval.
        var orchestration = await ResolveOrchestrationAsync(effectiveAgentId, activeModel, trimmedContent, supportsTools, cancellationToken).ConfigureAwait(false);

        return new ResolvedTurn(activeModel, resolved, orchestration, supportsThinking, supportsTools);
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
        string ActiveModel,
        ResolvedAgentRuntime? Resolved,
        ResolvedOrchestration? Orchestration,
        bool SupportsThinking,
        bool SupportsTools);
}
