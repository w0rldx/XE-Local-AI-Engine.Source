namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;

/// <summary>
///     Default <see cref="INodeChatRegenerationService" />. Reuses the shared runner/pump/dispatcher the local send
///     path uses (<see cref="NodeChatStreamService" />); the only structural differences are that the assistant
///     message is a sibling VARIANT (minted via <see cref="INodeChatPersistenceService.CreateMessageVariantAsync" />,
///     not a fresh placeholder) and the conversation context is built UP TO the parent user turn so the regenerate
///     answers the same question without seeing the original answer or other sibling variants.
/// </summary>
public sealed class NodeChatRegenerationService(
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
    IGgufModelCapabilityResolver ggufModelCapabilityResolver,
    ILocalDefaultChatModelResolver localDefaultChatModelResolver,
    IMemoryExtractionDispatcher memoryExtractionDispatcher,
    TimeProvider timeProvider,
    ILogger<NodeChatRegenerationService> logger) : INodeChatRegenerationService
{
    private const int AgentDefinitionVersion = 1;
    private const string AssistantRole = "assistant";
    private const string UserRole = "user";

    public IAsyncEnumerable<ChatStreamEvent> RegenerateAsync(Guid conversationId,
        Guid originalMessageId,
        string? reasoningEffort = null,
        bool useLocalTools = false,
        IReadOnlyDictionary<Guid, Guid>? selectedPath = null,
        CancellationToken cancellationToken = default)
    {
        return RegenerateCoreAsync(conversationId, originalMessageId, reasoningEffort, useLocalTools, selectedPath, cancellationToken);
    }

    private async IAsyncEnumerable<ChatStreamEvent> RegenerateCoreAsync(Guid conversationId,
        Guid originalMessageId,
        string? reasoningEffort,
        bool useLocalTools,
        IReadOnlyDictionary<Guid, Guid>? requestedSelectedPath,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        // Reject regeneration on a remote-origin (view-only) conversation before any persistence. Authoritative
        // guard; throwing here propagates to the hub caller, same as the send path.
        await mutationGuard.EnsureMutableAsync(conversationId, cancellationToken).ConfigureAwait(false);

        var conversation = await persistence.GetConversationAsync(conversationId, cancellationToken).ConfigureAwait(false)
                           ?? throw new InvalidOperationException("The node chat conversation was not found.");

        // Same precedence as the send path: a request-supplied selection is persisted and used; otherwise the
        // already-persisted conversation selection drives the pre-cutoff context.
        var selectedPath = requestedSelectedPath is not null
            ? await persistence.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest(conversationId, requestedSelectedPath, NowUnixMilliseconds()), cancellationToken).ConfigureAwait(false)
            : conversation.SelectedPath;

        var original = conversation.Messages.FirstOrDefault(message => message.MessageId == originalMessageId)
                       ?? throw new InvalidOperationException("The assistant message to regenerate was not found.");

        var newMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var startedAtUtc = NowUnixMilliseconds();

        // Resolve the model and the effective agent BEFORE minting the variant placeholder, because the variant is
        // stamped with the resolved agent's attribution (id + freshly-snapshotted display name). The resolve reads only
        // conversation/original/selectedPath, so the hoist is safe and the emitted SSE order (AssistantPending ->
        // AssistantQueued) is unchanged.
        var resolution = await ResolveTurnAsync(conversation, original, cancellationToken).ConfigureAwait(false);

        // Reuse the backend mint: creates the sibling placeholder (pending, shared variant_group_id, parent copied
        // from the original) — never an in-place overwrite. We do NOT duplicate mint logic here. The variant carries
        // the resolved agent's attribution so the pending variant already shows the agent name.
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
                              // package built below). Survives reload off the metadata blob.
                              ReasoningEffort: resolution.Resolved?.ReasoningEffort ?? reasoningEffort),
                          cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("The assistant message to regenerate was not found.");

        var placeholder = variant.Variant;
        var correlation = new NodeChatMessageCorrelation(conversationId, placeholder.MessageId, requestId);
        var sequence = new NodeChatStreamSequence();

        yield return ToMessageEvent(ChatStreamEventTypes.AssistantPending, correlation, placeholder, sequence.Next());

        // Queued until the collision-queue lease is acquired in RunInvocationAsync; transitions to Streaming only
        // when the invocation actually starts, so a turn waiting behind another invocation reads "queued".
        var queuedMessage = await persistence.MarkAssistantQueuedAsync(correlation, NowUnixMilliseconds(), cancellationToken).ConfigureAwait(false);
        yield return ToMessageEvent(ChatStreamEventTypes.AssistantQueued, correlation, queuedMessage, sequence.Next());

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var registration = cancellationRegistry.Register(correlation, () =>
        {
            invocationRunner.Cancel(requestId);
            linkedCancellation.Cancel();
        });

        var stateChannel = Channel.CreateUnbounded<InvocationState>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var eventChannel = Channel.CreateUnbounded<ChatStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            // Three producers write this channel concurrently: the streaming-transition emit in RunInvocationAsync,
            // the delta/terminal emits in PumpInvocationStatesAsync, and the tool-call lifecycle emits in
            // OnToolCallLifecycleChanged. SingleWriter must be false.
            SingleWriter = false
        });

        void OnInvocationStateChanged(object? _, InvocationStateChangedEventArgs args)
        {
            if (args.State.InvocationId == requestId)
            {
                stateChannel.Writer.TryWrite(args.State);
            }
        }

        // Accumulates the ordered reasoning/tool interleave so the regenerated turn persists parts[] (the reload
        // render source), symmetric with the send path. Fed by the tool handler below and the pump's reasoning deltas.
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
        // front (ResolveTurnAsync) so the variant could be stamped with the resolved agent's attribution; reuse those
        // results here unchanged.
        var activeModel = resolution.ActiveModel;
        var resolved = resolution.Resolved;
        var orchestration = resolution.Orchestration;

        // Symmetric with the send path (NodeChatStreamService): offer tools to the loopback agent only when the
        // client asked AND the node has the tool engine enabled AND the active model advertises the Ollama tools
        // capability. When offered, the catalog's local tools travel in the runtime package as the offer list; the
        // invocation factory resolves the matching executables from the registry by name. A bound definition narrows
        // that offer to its allowed set (and the resolver already withheld the offer for a non-tools model).
        var enableTools = await runtimeSettings.GetEnableToolsAsync(cancellationToken).ConfigureAwait(false);
        var offerTools = useLocalTools && enableTools && resolution.SupportsTools;
        var allowedTools = offerTools
            ? resolved?.AllowedTools ?? localToolOfferProvider.GetOfferedTools(activeModel)
            : null;

        var package = runtimePackageBuilder.Build(new LocalChatRuntimePackageRequest(requestId,
            conversationId,
            resolved?.ResolvedSystemPrompt ?? LoadResolvedSystemPrompt(localChatOptions.Value),
            BuildRegenerationContext(conversation, original, selectedPath),
            resolution.EffectiveModel,
            resolved?.AgentDefinitionVersion ?? AgentDefinitionVersion,
            LocalChatLoopbackDefaults.ClientNodeId,
            allowedTools,
            RequestedCapabilities: [LocalChatLoopbackDefaults.RequestedCapability],
            ReasoningEffort: resolved?.ReasoningEffort ?? reasoningEffort,
            OrchestrationSpec: orchestration?.Spec,
            SupportsThinking: resolution.SupportsThinking,
            Skills: resolved?.Skills));

        // Post-run adaptive-memory hook (symmetric with the send path): fired once when the pump persists a
        // Completed/Failed terminal, ONLY when the resolved agent has the playbook enabled AND opts into extraction. A
        // regenerated turn is still a completed assistant turn worth learning from — but a retrieval-only agent
        // (extraction off) still uses its memory while mining no new candidates. Built here so it closes over the run
        // context this service holds.
        var onTerminal = resolution.Resolved is { PlaybookEnabled: true, MemoryExtractionEnabled: true } memoryAgent
            ? BuildMemoryExtractionHook(memoryAgent, conversation, original, selectedPath, package, resolution.EffectiveModel)
            : null;

        var pumpTask = PumpInvocationStatesAsync(stateChannel.Reader,
            eventChannel.Writer,
            correlation,
            // Stamp the FINAL persisted variant model from the effective model (the pump terminalizes from this
            // requestedModel) so the stored attribution reflects the model that actually reran, not original.Model.
            resolution.EffectiveModel,
            sequence,
            parts,
            onTerminal,
            linkedCancellation.Token);
        var runTask = RunInvocationAsync(package,
            placeholder.MessageId,
            stateChannel.Writer,
            eventChannel.Writer,
            correlation,
            requestId,
            sequence,
            resolution.RequiresInstalledChatModel,
            linkedCancellation.Token);

        try
        {
            await foreach (var streamEvent in eventChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return streamEvent;
            }
        }
        finally
        {
            eventDispatcher.InvocationStateChanged -= OnInvocationStateChanged;
            eventDispatcher.ToolCallLifecycleChanged -= OnToolCallLifecycleChanged;
            await linkedCancellation.CancelAsync().ConfigureAwait(false);

            try
            {
                await Task.WhenAll(runTask, pumpTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The terminal cancelled/interrupted event is persisted by the pump.
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
        // Queue behind any in-flight invocation (local or platform) under the shared lease, rather than failing
        // the turn; the lease holds the slot for this run. Cancelling while queued aborts the wait and the run
        // is terminalized as cancelled below.
        IAsyncDisposable? lease = null;

        try
        {
            lease = await eventDispatcher.ReportInvocationAssignedAsync(package, cancellationToken).ConfigureAwait(false);

            var streamingMessage = await persistence.MarkAssistantStreamingAsync(correlation, NowUnixMilliseconds(), cancellationToken).ConfigureAwait(false);
            await eventWriter.WriteAsync(ToMessageEvent(ChatStreamEventTypes.AssistantStreaming, correlation, streamingMessage, sequence.Next()), cancellationToken).ConfigureAwait(false);

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
                "Invocation timed out or was cancelled",
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

    private async Task PumpInvocationStatesAsync(ChannelReader<InvocationState> stateReader,
        ChannelWriter<ChatStreamEvent> eventWriter,
        NodeChatMessageCorrelation correlation,
        string? requestedModel,
        NodeChatStreamSequence sequence,
        NodeChatPartAccumulator parts,
        Action<InvocationState, NodeChatPumpTerminalResult>? onTerminal,
        CancellationToken cancellationToken)
    {
        // The shared pump owns all persistence (INTO the variant row, by correlation); this only fans the
        // persisted results out as SSE events. The sequence is shared with the streaming-transition event from
        // RunInvocationAsync so all events stay monotonically ordered.
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
                    // Empty snapshot -> null so the persisted parts are left untouched rather than overwritten empty.
                    var snapshot = parts.HasParts ? parts.Snapshot() : null;
                    var terminal = await invocationPump.TerminalizeAsync(correlation, state, requestedModel, snapshot).ConfigureAwait(false);
                    terminalPersisted = true;

                    // Post-run adaptive memory: hand the just-persisted terminal to the background extraction hook before
                    // the SSE write. The hook only schedules work on its own scope — it never blocks or throws here.
                    onTerminal?.Invoke(state, terminal);

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
    ///     Builds the regeneration context: every completed message UP TO AND INCLUDING the USER turn that precedes
    ///     the turn being regenerated, excluding the original assistant answer and any sibling variants. Assistant
    ///     placeholders are minted with no parent_message_id (the variant's parent is the prior assistant, not the
    ///     user turn), so a parent walk cannot reach the user turn. Instead the cutoff is the latest USER turn
    ///     strictly before the EARLIEST member of the original's variant group — every member of that group (the
    ///     original answer and all sibling variants) sorts at or after that user turn and is therefore excluded.
    ///     When no preceding user turn exists, falls back to everything strictly before the earliest group member.
    /// </summary>
    private static IReadOnlyList<ConversationMessageDto> BuildRegenerationContext(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto original,
        IReadOnlyDictionary<Guid, Guid>? selectedPath)
    {
        var cutoffSequence = ResolvePrecedingUserTurnCutoff(conversation, original);

        // A prior turn before the cutoff may itself have variants; collapse those to the selected path so the
        // regenerate sees the same chosen branch the send path would. The group being regenerated already sorts
        // at/after the cutoff (see ResolvePrecedingUserTurnCutoff), so it is excluded by the sequence filter
        // regardless of which member the resolver would otherwise pick.
        var selected = SelectedPathResolver.Resolve(conversation.Messages, selectedPath);

        var messages = selected
                       .Where(message => message.Sequence <= cutoffSequence
                                         && !string.IsNullOrWhiteSpace(message.Content)
                                         && string.Equals(message.Status, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal))
                       .OrderBy(static message => message.Sequence)
                       .Select(static (message, index) => new ConversationMessageDto
                       {
                           Id = message.MessageId,
                           Role = string.Equals(message.Role, AssistantRole, StringComparison.OrdinalIgnoreCase) ? MessageRole.Assistant : MessageRole.User,
                           Content = message.Content,
                           Thinking = message.Reasoning,
                           ModelUsed = message.Model,
                           SortOrder = index
                       })
                       .ToList();

        return messages;
    }

    /// <summary>
    ///     Resolves the cutoff sequence (inclusive) for the regeneration context: the latest USER turn strictly
    ///     before the earliest member of the original's variant group. The group spans the original answer and every
    ///     sibling variant, so anchoring on its earliest member keeps all of them out of context whichever member is
    ///     being regenerated. With no preceding user turn, returns the slot before the earliest group member so the
    ///     context is everything that came before — never the answer being replaced.
    /// </summary>
    private static int ResolvePrecedingUserTurnCutoff(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto original)
    {
        var earliestGroupSequence = original.VariantGroupId is { } groupId
            ? conversation.Messages.Where(message => message.VariantGroupId == groupId)
                          .Select(message => message.Sequence)
                          .DefaultIfEmpty(original.Sequence)
                          .Min()
            : original.Sequence;

        var precedingUserTurn = conversation.Messages
                                            .Where(message => message.Sequence < earliestGroupSequence
                                                              && string.Equals(message.Role, UserRole, StringComparison.OrdinalIgnoreCase))
                                            .OrderByDescending(message => message.Sequence)
                                            .FirstOrDefault();

        return precedingUserTurn?.Sequence ?? earliestGroupSequence - 1;
    }

    /// <summary>
    ///     Builds the post-run memory-extraction hook for a regenerated turn (symmetric with the send path). It closes
    ///     over the resolved agent, the loaded conversation (temp-chat flag + pre-cutoff user turns), the original turn,
    ///     and the runtime package (config hash). On a Completed/Failed terminal it assembles the metadata-only
    ///     execution-log telemetry plus the content-bearing run input and hands both to the background dispatcher.
    /// </summary>
    private Action<InvocationState, NodeChatPumpTerminalResult> BuildMemoryExtractionHook(ResolvedAgentRuntime resolved,
        NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto original,
        IReadOnlyDictionary<Guid, Guid>? selectedPath,
        RuntimePackage package,
        string? requestedModel)
    {
        return (state, terminal) =>
        {
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
                // Exception TYPE NAME only when present — never the sanitized message text.
                failed ? state.FailureCategory?.ToString() : null);

            var run = new MemoryExtractionRunInput(resolved.AgentDefinitionId,
                conversation.ConversationId,
                terminal.Persisted.MessageId,
                CollectUserTurns(conversation, original, selectedPath),
                state.StreamedContent,
                failed,
                state.Error,
                conversation.MemoryExcluded);

            memoryExtractionDispatcher.Dispatch(telemetry, run);
        };
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
        return BuildRegenerationContext(conversation, original, selectedPath)
               .Where(static message => message.Role == MessageRole.User && !string.IsNullOrWhiteSpace(message.Content))
               .Select(static message => new MemoryExtractionTurn(message.Content))
               .ToArray();
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
    ///     up front so the regenerated variant can be stamped with the resolved agent's attribution. The effective-agent
    ///     precedence reuses the ORIGINAL turn's recorded agent so a rerun stays on the same persona:
    ///     <c>original.AgentDefinitionId ?? conversation.AgentDefinitionId ?? (memoized) Default Assistant id</c>. The
    ///     attribution name is re-resolved (picks up a rename); when the agent was deleted the resolver returns null and
    ///     the variant falls back to the original's stored name. The relevance-retrieval query is the user turn that
    ///     precedes the regenerated turn (same cutoff anchor as the regeneration context).
    /// </summary>
    private async Task<ResolvedTurn> ResolveTurnAsync(NodeChatConversationDto conversation,
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

        // Resolve the active model's advertised capabilities once (cache-first) so the think field and tool offer are
        // gated symmetrically with the send path: a non-thinking model never receives the think field (avoiding the
        // Ollama 400) and a non-tools model is offered no tools. Unknown/offline resolves to NOT-capable (safe default).
        var (supportsThinking, supportsTools) = await ResolveModelCapabilitiesAsync(activeModel, cancellationToken).ConfigureAwait(false);

        var resolved = await agentDefinitionResolver.ResolveAsync(effectiveAgentId, activeModel, retrievalQuery, supportsTools, honorModelProfile: !userPickedConcreteModel, cancellationToken).ConfigureAwait(false);
        var orchestration = await ResolveOrchestrationAsync(effectiveAgentId, activeModel, retrievalQuery, supportsTools, cancellationToken).ConfigureAwait(false);

        // The single source of truth for the model that actually reruns this turn (agent pin when honored, else the
        // active model). Stamped into the variant placeholder, the runtime package, and the persisted attribution so
        // the variant's label can never disagree with what ran — symmetric with the send path.
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
        // tool loop). Mirrors NodeChatStreamService.ResolveModelCapabilitiesAsync so regenerate matches the send path.
        if (CodexModelCatalog.IsCodexModel(activeModel))
        {
            return (SupportsThinking: true, CodexProviderCapabilities.V0.SupportsToolCalling);
        }

        // A llama.cpp (GGUF) model has no Ollama entry — and in desktop mode there is no Ollama daemon — so an
        // /api/show classification would fail or stall. When the active model is an installed GGUF, read the
        // capabilities detected offline from its chat template (cheap, cached) instead of probing Ollama, mirroring the
        // send path (NodeChatStreamService.ResolveModelCapabilitiesAsync). A non-GGUF model returns null here and falls
        // through to the Ollama classification below.
        var ggufCapabilities = await ggufModelCapabilityResolver
                                     .TryResolveAsync(activeModel, cancellationToken)
                                     .ConfigureAwait(false);
        if (ggufCapabilities is { } caps)
        {
            return (caps.SupportsThinking, caps.SupportsTools);
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
    ///     Mirrors <c>NodeChatStreamService.ResolveOrchestrationAsync</c>: resolves a compiled orchestration spec for a
    ///     bound orchestrator definition (orchestration), or <c>null</c> to rerun single-agent. Only a bound conversation
    ///     triggers the extra record fetch, so the single-agent path stays byte-identical.
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

    /// <summary>
    ///     The content of the latest USER turn strictly before the original's variant group — the question the
    ///     regenerate re-answers, used as the relevance-retrieval query. Mirrors the cutoff anchor used by
    ///     <see cref="ResolvePrecedingUserTurnCutoff" />; returns <c>null</c> when no such user turn exists, so the
    ///     resolver falls back to the full static prepend.
    /// </summary>
    private static string? ResolvePrecedingUserTurnContent(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto original)
    {
        var earliestGroupSequence = original.VariantGroupId is { } groupId
            ? conversation.Messages.Where(message => message.VariantGroupId == groupId)
                          .Select(message => message.Sequence)
                          .DefaultIfEmpty(original.Sequence)
                          .Min()
            : original.Sequence;

        return conversation.Messages
                           .Where(message => message.Sequence < earliestGroupSequence
                                             && string.Equals(message.Role, UserRole, StringComparison.OrdinalIgnoreCase))
                           .OrderByDescending(message => message.Sequence)
                           .Select(message => message.Content)
                           .FirstOrDefault();
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

    /// <summary>The up-front per-turn resolution shared by variant stamping and runtime-package construction.</summary>
    private sealed record ResolvedTurn(
        string? ActiveModel,
        string? EffectiveModel,
        ResolvedAgentRuntime? Resolved,
        ResolvedOrchestration? Orchestration,
        bool SupportsThinking,
        bool SupportsTools,
        bool RequiresInstalledChatModel);
}
