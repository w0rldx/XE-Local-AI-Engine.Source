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
    IOptions<KnowledgeBaseOptions> knowledgeOptions,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IToolApprovalPolicy toolApprovalPolicy,
    ILogger<NodeChatRegenerationService> logger) : INodeChatRegenerationService
{
    private const int AgentDefinitionVersion = 1;
    private const string AssistantRole = "assistant";
    private const string UserRole = "user";

    public IAsyncEnumerable<ChatStreamEvent> RegenerateAsync(Guid conversationId,
        Guid originalMessageId,
        string? reasoningEffort = null,
        bool useLocalTools = false,
        bool useKnowledgeBase = false,
        IReadOnlyDictionary<Guid, Guid>? selectedPath = null,
        CancellationToken cancellationToken = default)
    {
        return RegenerateCoreAsync(conversationId, originalMessageId, reasoningEffort, useLocalTools, useKnowledgeBase, selectedPath, cancellationToken);
    }

    private async IAsyncEnumerable<ChatStreamEvent> RegenerateCoreAsync(Guid conversationId,
        Guid originalMessageId,
        string? reasoningEffort,
        bool useLocalTools,
        bool useKnowledgeBase,
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

        // The variant row now exists as Pending, but run ownership (the pump + runner + their protective finally) is not
        // wired until further below. If the client disconnects in this window — including during the awaited
        // GetEnableToolsAsync / package build before the tasks are created — the iterator is disposed and the variant
        // would otherwise sit Pending/Queued until the restart reaper. This guard terminalizes it to Interrupted on any
        // pre-ownership teardown; once ownership is established it becomes a no-op and the pump owns the terminal. Shared
        // with the send path (NodeChatStreamService) so both front doors behave identically.
        await using var preOwnershipGuard = new PreOwnershipTerminalizationGuard(persistence, correlation, timeProvider, logger);

        yield return ToMessageEvent(ChatStreamEventTypes.AssistantPending, correlation, placeholder, sequence.Next());

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

        yield return ToMessageEvent(ChatStreamEventTypes.AssistantQueued, correlation, queuedMessage, sequence.Next());

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
        var eventChannel = Channel.CreateUnbounded<ChatStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            // Four producers write this channel concurrently: the streaming-transition emit in RunInvocationAsync,
            // the delta/terminal emits in the invocation-state pump, the tool-call lifecycle emits in
            // OnToolCallLifecycleChanged, and the turn-notice emits in OnTurnNoticeChanged. SingleWriter must be false.
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
                ChatStreamEventMapper.AccumulateToolPart(parts, args.Payload, toolSequence);
                eventChannel.Writer.TryWrite(ChatStreamEventMapper.ToolCallEvent(correlation.ConversationId, correlation.MessageId, correlation.RequestId, args.Payload, NowUnixMilliseconds(),
                    toolSequence));
            }
        }

        void OnTurnNoticeChanged(object? _, TurnNoticeChangedEventArgs args)
        {
            if (args.Payload.InvocationId == requestId)
            {
                var noticeSequence = sequence.Next();
                ChatStreamEventMapper.AccumulateNotice(parts, args.Payload, noticeSequence);
                eventChannel.Writer.TryWrite(ChatStreamEventMapper.NoticeEvent(correlation.ConversationId, correlation.MessageId, correlation.RequestId, args.Payload, NowUnixMilliseconds(),
                    noticeSequence));
            }
        }

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

        // Subscriptions live INSIDE the try so an OCE thrown by the awaited GetEnableToolsAsync / package build below
        // (client disconnect) can never leak the handlers — the finally always detaches them. Previously the try started
        // AFTER these subscriptions, so a pre-task teardown left them attached to the singleton dispatcher for the
        // process lifetime.
        eventDispatcher.InvocationStateChanged += OnInvocationStateChanged;
        eventDispatcher.ToolCallLifecycleChanged += OnToolCallLifecycleChanged;
        eventDispatcher.TurnNoticeChanged += OnTurnNoticeChanged;

        try
        {
            // Symmetric with the send path (NodeChatStreamService): offer tools to the loopback agent only when the
            // client asked AND the node has the tool engine enabled AND the active model advertises the Ollama tools
            // capability. When offered, the catalog's local tools travel in the runtime package as the offer list; the
            // invocation factory resolves the matching executables from the registry by name. A bound definition narrows
            // that offer to its allowed set (and the resolver already withheld the offer for a non-tools model).
            var enableTools = await runtimeSettings.GetEnableToolsAsync(cancellationToken).ConfigureAwait(false);
            var offerTools = useLocalTools && enableTools && resolution.SupportsTools;
            // A bound definition's AllowedTools already ran through the node approval policy in the resolver; the
            // unbound/deleted-agent fallback builds the raw offer here, so it applies the SAME node policy (tighten-only)
            // to avoid a bypass. Permissive floor = identity, so an unconfigured node stays byte-identical to today.
            var allowedTools = !offerTools
                ? null
                : resolved?.AllowedTools
                  ??
                  [
                      .. localToolOfferProvider.GetOfferedTools(activeModel, resolution.EffectiveModelIsCloud)
                                               .Select(tool => tool with
                                               {
                                                   RequiresApproval = toolApprovalPolicy.RequiresApproval(tool.Name, tool.Category, tool.RequiresApproval)
                                               })
                  ];

            // Knowledge-base grounding parity with the send path (OPP-05 / UX-04): a regenerated plain-chat turn honors
            // the same opt-in knowledge grounding + cloud-egress gate the send path applies, so a rerun does not silently
            // lose grounding + its sources strip. Agent mode reaches the KB through the gated search_knowledge_base tool
            // (offerTools), so inline grounding is plain-chat only — mirroring NodeChatStreamService. The retrieval query
            // is the user turn the regenerate re-answers (same cutoff anchor as the regeneration context).
            ConversationMessageDto? knowledgeContext = null;
            IReadOnlyList<NodeChatMessageSource>? knowledgeSources = null;
            if (useKnowledgeBase && !offerTools)
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
                }
                else
                {
                    var retrievalQuery = ResolvePrecedingUserTurnContent(conversation, original);
                    if (!string.IsNullOrWhiteSpace(retrievalQuery))
                    {
                        var knowledge = await BuildKnowledgeContextMessageAsync(retrievalQuery, runCancellation.Token).ConfigureAwait(false);
                        if (knowledge is not null)
                        {
                            knowledgeContext = knowledge.Message;
                            knowledgeSources = knowledge.Sources;
                        }
                    }
                }
            }

            var package = runtimePackageBuilder.Build(new LocalChatRuntimePackageRequest(requestId,
                conversationId,
                resolved?.ResolvedSystemPrompt ?? LoadResolvedSystemPrompt(localChatOptions.Value),
                BuildRegenerationContext(conversation, original, selectedPath, knowledgeContext),
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
                ? ChatMemoryExtractionHook.Build(memoryExtractionDispatcher,
                    memoryAgent,
                    conversation.ConversationId,
                    conversation.MemoryExcluded,
                    package,
                    resolution.EffectiveModel,
                    () => CollectUserTurns(conversation, original, selectedPath))
                : null;

            pumpTask = invocationStatePump.PumpAsync(stateChannel.Reader,
                eventChannel.Writer,
                correlation,
                // Stamp the FINAL persisted variant model from the effective model (the pump terminalizes from this
                // requestedModel) so the stored attribution reflects the model that actually reran, not original.Model.
                resolution.EffectiveModel,
                sequence,
                parts,
                onTerminal,
                runCancellation.Token,
                // Knowledge-base sources that grounded this regenerated turn (OPP-05 and UX-04) are stamped onto the
                // variant metadata by the pump. A rerun that used no knowledge base passes none here.
                knowledgeSources);
            runTask = RunInvocationAsync(package,
                placeholder.MessageId,
                stateChannel.Writer,
                eventChannel.Writer,
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
            await foreach (var streamEvent in eventChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return streamEvent;
            }
        }
        finally
        {
            // Do NOT cancel runCancellation here on a client disconnect: let runTask/pumpTask drain to the runner's true
            // terminal so persistence follows the runner's lifecycle, not the client connection's. A genuine user cancel
            // already tripped runCancellation via the registry.
            if (pumpTask is not null && runTask is not null)
            {
                // Observe the pump first: on a GPTAUD-07 persistence fault it faults here; cancel the run so the
                // still-generating runner stops promptly rather than producing output that can no longer be persisted. A
                // user cancel or normal completion leaves the pump task completed (not faulted).
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

            // Unsubscribe AFTER draining the tasks, never before: the runner may fire the terminal InvocationStateChanged
            // (the Completed terminal) after the SSE loop exits. Detaching first would drop it, ending the pump with no
            // terminal and falsely persisting the variant Interrupted.
            eventDispatcher.InvocationStateChanged -= OnInvocationStateChanged;
            eventDispatcher.ToolCallLifecycleChanged -= OnToolCallLifecycleChanged;
            eventDispatcher.TurnNoticeChanged -= OnTurnNoticeChanged;
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
            if (!string.Equals(streamingMessage.Status, NodeChatMessageStatusValues.Streaming, StringComparison.Ordinal))
            {
                // The variant was finalized (cancelled) before streaming could start. Do not stream into a terminal
                // message or run the model: return so the finally completes the state channel and the pump terminalizes.
                return;
            }

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
        IReadOnlyDictionary<Guid, Guid>? selectedPath,
        ConversationMessageDto? knowledgeContext = null)
    {
        var cutoffSequence = ResolvePrecedingUserTurnCutoff(conversation, original);

        // A prior turn before the cutoff may itself have variants; collapse those to the selected path so the
        // regenerate sees the same chosen branch the send path would. The group being regenerated already sorts
        // at/after the cutoff (see ResolvePrecedingUserTurnCutoff), so it is excluded by the sequence filter
        // regardless of which member the resolver would otherwise pick.
        var selected = SelectedPathResolver.Resolve(conversation.Messages, selectedPath);

        // The synthetic knowledge-base grounding message (plain-chat only) is prepended so the model reads its content
        // before the conversation history — mirroring the send path (NodeChatStreamService.BuildConversationContext). It
        // takes the first slot and the history shifts down by one; null on every non-grounded rerun.
        var leadingOffset = knowledgeContext is not null ? 1 : 0;

        var messages = selected
                       .Where(message => message.Sequence <= cutoffSequence
                                         && !string.IsNullOrWhiteSpace(message.Content)
                                         && string.Equals(message.Status, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal))
                       .OrderBy(static message => message.Sequence)
                       .Select((message, index) => new ConversationMessageDto
                       {
                           Id = message.MessageId,
                           Role = string.Equals(message.Role, AssistantRole, StringComparison.OrdinalIgnoreCase) ? MessageRole.Assistant : MessageRole.User,
                           Content = message.Content,
                           Thinking = message.Reasoning,
                           ModelUsed = message.Model,
                           SortOrder = index + leadingOffset
                       })
                       .ToList();

        return knowledgeContext is null
            ? messages
            : [knowledgeContext with { SortOrder = 0 }, .. messages];
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

    // Retrieves the top-k fused knowledge-base hits for the regenerate's question and composes them into ONE fenced
    // untrusted context message (OPP-05), returning it alongside the provenance of the inlined hits (UX-04). Runs the
    // hybrid search in a FRESH DI scope (IKnowledgeSearchService is scoped, driving a request-scoped connection) —
    // byte-for-byte the send path (NodeChatStreamService.BuildKnowledgeContextMessageAsync). Returns null when grounding
    // produces nothing: a blank/oversized query, no matching chunks, an empty compose, or ANY retrieval failure
    // (degrades gracefully — the rerun proceeds without knowledge context). The caller applied the cloud-egress gate.
    private async Task<KnowledgeChatMessage?> BuildKnowledgeContextMessageAsync(string query, CancellationToken cancellationToken)
    {
        var validation = KnowledgeQueryLimits.ValidateAndNormalize(query, out var normalizedQuery);
        if (validation != KnowledgeQueryValidation.Valid)
        {
            return null;
        }

        try
        {
            var limit = localChatOptions.Value.KnowledgeChatTopK;
            var searchRequest = new KnowledgeSearchRequest(normalizedQuery, limit, DocumentId: null, ExpandNeighbors: false);

            await using var scope = scopeFactory.CreateAsyncScope();
            var searchService = scope.ServiceProvider.GetRequiredService<IKnowledgeSearchService>();
            var result = await searchService.SearchAsync(searchRequest, cancellationToken).ConfigureAwait(false);

            if (result.Results.Count == 0)
            {
                return null;
            }

            var composed = KnowledgeChatContextComposer.Compose(result.Results, localChatOptions.Value.MaxInlinedKnowledgeChars);
            if (composed is null)
            {
                return null;
            }

            var message = new ConversationMessageDto
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.User,
                Content = composed.Context,
                SortOrder = 0
            };
            return new KnowledgeChatMessage(message, composed.Sources);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Retrieval is a best-effort supplement: a failure (embedding provider down, connection error, etc.) must
            // never fail the rerun. Log and proceed with no knowledge context.
            logger.LogWarning(exception, "Knowledge-base grounding failed for the regenerated plain-chat turn; proceeding without it.");
            return null;
        }
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
        return ChatStreamEventMapper.MessageEvent(type, correlation, message, NowUnixMilliseconds(), sequence, delta, reasoningDelta, inputTokens, outputTokens, totalTokens, reasoningTokens);
    }

    private long NowUnixMilliseconds()
    {
        return timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }

    // The composed knowledge-base context message plus the provenance of the hits inlined into it (so the terminal
    // variant records them as sources). Mirrors the send path's private KnowledgeChatMessage record.
    private sealed record KnowledgeChatMessage(ConversationMessageDto Message, IReadOnlyList<NodeChatMessageSource> Sources);
}
