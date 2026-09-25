namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Default <see cref="INodeChatRegenerationService" />, reusing the runner, pump and dispatcher of the send path.
/// </summary>
/// <remarks>
///     The only structural differences from <see cref="NodeChatStreamService" /> are that the assistant message is a
///     sibling VARIANT, minted via <see cref="INodeChatPersistenceService.CreateMessageVariantAsync" /> rather than a
///     fresh placeholder, and that the conversation context is built UP TO the parent user turn, so the regenerate
///     answers the same question without seeing the original answer or other sibling variants.
/// </remarks>
public sealed class NodeChatRegenerationService : INodeChatRegenerationService
{
    private const int AgentDefinitionVersion = 1;

    // Mirrors the send path (NodeChatStreamService.PreRunCancelledMessage): a cancel that lands while the turn is still
    // waiting for the shared collision-queue lease, before the invocation — and therefore its own timeout — ever starts.
    private const string PreRunCancelledMessage = "Stopped before the response started (cancelled while queued).";
    private const string AssistantRole = "assistant";
    private const string UserRole = "user";
    private readonly INodeChatPersistenceService _persistence;
    private readonly ChatInvocationStatePump _invocationStatePump;
    private readonly ChatTurnResolver _turnResolver;
    private readonly INodeChatMutationGuard _mutationGuard;
    private readonly ILocalChatRuntimePackageBuilder _runtimePackageBuilder;
    private readonly IInvocationRunner _invocationRunner;
    private readonly IWorkerEventDispatcher _eventDispatcher;
    private readonly IOptions<LocalChatAgentOptions> _localChatOptions;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly INodeChatStreamCancellationRegistry _cancellationRegistry;
    private readonly ILocalToolOfferProvider _localToolOfferProvider;
    private readonly IDefaultAgentProvider _defaultAgentProvider;
    private readonly INodeSettingsStore _nodeSettingsStore;
    private readonly ILocalDefaultChatModelResolver _localDefaultChatModelResolver;
    private readonly IMemoryExtractionDispatcher _memoryExtractionDispatcher;
    private readonly IConversationMaintenanceDispatcher _conversationMaintenanceDispatcher;
    private readonly IChatTurnContextBuilder _turnContextBuilder;
    private readonly IOptions<KnowledgeBaseOptions> _knowledgeOptions;
    private readonly IOptions<ChatStreamBudgetOptions> _streamBudgetOptions;
    private readonly TimeProvider _timeProvider;
    private readonly IToolApprovalPolicy _toolApprovalPolicy;
    private readonly ILogger<NodeChatRegenerationService> _logger;

    public NodeChatRegenerationService(INodeChatPersistenceService persistence,
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
        IConversationMaintenanceDispatcher conversationMaintenanceDispatcher,
        IChatTurnContextBuilder turnContextBuilder,
        IOptions<KnowledgeBaseOptions> knowledgeOptions,
        IOptions<ChatStreamBudgetOptions> streamBudgetOptions,
        TimeProvider timeProvider,
        IToolApprovalPolicy toolApprovalPolicy,
        ILogger<NodeChatRegenerationService> logger)
    {
        _persistence = persistence;
        _invocationStatePump = invocationStatePump;
        _turnResolver = turnResolver;
        _mutationGuard = mutationGuard;
        _runtimePackageBuilder = runtimePackageBuilder;
        _invocationRunner = invocationRunner;
        _eventDispatcher = eventDispatcher;
        _localChatOptions = localChatOptions;
        _runtimeSettings = runtimeSettings;
        _cancellationRegistry = cancellationRegistry;
        _localToolOfferProvider = localToolOfferProvider;
        _defaultAgentProvider = defaultAgentProvider;
        _nodeSettingsStore = nodeSettingsStore;
        _localDefaultChatModelResolver = localDefaultChatModelResolver;
        _memoryExtractionDispatcher = memoryExtractionDispatcher;
        _conversationMaintenanceDispatcher = conversationMaintenanceDispatcher;
        _turnContextBuilder = turnContextBuilder;
        _knowledgeOptions = knowledgeOptions;
        _streamBudgetOptions = streamBudgetOptions;
        _timeProvider = timeProvider;
        _toolApprovalPolicy = toolApprovalPolicy;
        _logger = logger;
    }

    public IAsyncEnumerable<ChatStreamEvent> RegenerateAsync(Guid conversationId,
        Guid originalMessageId,
        string? reasoningEffort = null,
        bool useLocalTools = false,
        bool useKnowledgeBase = false,
        IReadOnlyDictionary<Guid, Guid>? selectedPath = null,
        SamplingOptions? samplingOptions = null,
        CancellationToken cancellationToken = default)
    {
        // The sampling seed rides the wire as a string, so a malformed value is rejected here rather than dropped
        // deeper in the invocation mapping. A null sampling block always parses, keeping the no-override path intact.
        if (!SeedValue.TryParse(samplingOptions?.Seed, out _, out var seedError))
        {
            throw new NodeChatInvalidRequestException(seedError);
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
        var turn = await LoadRegenerationTurnAsync(conversationId, originalMessageId, requestedSelectedPath, cancellationToken);
        var conversation = turn.Conversation;
        var selectedPath = turn.SelectedPath;
        var original = turn.Original;

        var newMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var startedAtUtc = NowUnixMilliseconds();

        // Resolved BEFORE the variant placeholder is minted, because the variant is stamped with the resolved agent's
        // id and freshly snapshotted name. It reads only conversation, original and path, so the order is unchanged.
        var resolution = await ResolveTurnAsync(conversation, original, cancellationToken);

        var placeholder = await MintVariantAsync(conversationId, originalMessageId, newMessageId, requestId, startedAtUtc, resolution, reasoningEffort, cancellationToken);
        var correlation = new NodeChatMessageCorrelation
        {
            ConversationId = conversationId,
            MessageId = placeholder.MessageId,
            RequestId = requestId
        };
        var sequence = new NodeChatStreamSequence();

        // The variant is Pending but run ownership (pump, runner, their finally) is not wired yet, so a disconnect in
        // this window would leave it Pending until the restart reaper. The guard terminalizes it Interrupted, then no-ops.
        await using var preOwnershipGuard = new PreOwnershipTerminalizationGuard(_persistence, correlation, _timeProvider, _logger);

        yield return ToMessageEvent(ChatStreamEventTypes.AssistantPending, correlation, placeholder, sequence.Next());

        // A regenerate is a turn too, so only the invocation timeout is operator-controlled and at the TimeoutSettings
        // default the package and its config hash stay byte-identical. Loaded here because the events stamp the ceiling.
        var runtimeNodeSettings = await _nodeSettingsStore.LoadAsync(cancellationToken);

        // Queued until the collision-queue lease is acquired in RunInvocationAsync; transitions to Streaming only
        // when the invocation actually starts, so a turn waiting behind another invocation reads "queued".
        var queuedMessage = await _persistence.MarkAssistantQueuedAsync(correlation, NowUnixMilliseconds(), cancellationToken);
        if (!string.Equals(queuedMessage.Status, NodeChatMessageStatusValues.Queued, StringComparison.Ordinal))
        {
            // A cancel raced ahead of run ownership and finalized this variant before it could be queued. Emit the terminal
            // the row actually holds and abort — never run an already-finalized regenerate.
            yield return ToMessageEvent(ChatStreamEventMapper.TerminalEventType(queuedMessage.Status), correlation, queuedMessage, sequence.Next());
            yield break;
        }

        yield return ToMessageEvent(ChatStreamEventTypes.AssistantQueued, correlation, queuedMessage, sequence.Next(),
            invocationTimeoutSeconds: runtimeNodeSettings.MaxMessageRequestTimeoutSeconds);

        // The run and persistence lifecycle belongs to the runner, not the client connection: runCancellation is an
        // UNLINKED source, tripped only by a user cancel through the registry, which also cancels the runner's loop.
        using var runCancellation = new CancellationTokenSource();
        using var registration = _cancellationRegistry.Register(correlation, () =>
        {
            _invocationRunner.Cancel(requestId);
#pragma warning disable MA0045 // INodeChatStreamCancellationRegistry.Register takes a synchronous Action; a cancel callback has no async form to convert to.
            runCancellation.Cancel();
#pragma warning restore MA0045
        });

        var stateChannel = Channel.CreateUnbounded<InvocationState>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        // Six producers write this sink concurrently: the run transition, pump deltas and terminals, the tool-call
        // lifecycle, turn notices, approval requests and user questions. It is BOUNDED and never makes one wait.
        var eventSink = new ChatStreamEventSink(correlation, sequence, _streamBudgetOptions.Value, _timeProvider);

        // Accumulates the ordered reasoning/tool interleave so the regenerated turn persists parts[], the reload render
        // source. Fed by BOTH producers: the forwarder's tool/notice handlers and the pump loop's reasoning deltas.
        var parts = new NodeChatPartAccumulator();

        // Subscribed HERE so the scope covers every pre-ownership exit and no disconnect leaks handlers onto the
        // singleton dispatcher; it is disposed only AFTER the drain, or a late terminal would read as interrupted.
        using var eventSubscription = new ChatStreamEventForwarder(_eventDispatcher, correlation, requestId, stateChannel.Writer, eventSink, sequence, parts, _timeProvider);

        // The active model, effective agent and orchestration spec were computed up front by ResolveTurnAsync so the
        // variant could be stamped with the resolved agent's attribution; they are reused here unchanged.
        var activeModel = resolution.ActiveModel;
        var resolved = resolution.Resolved;
        var orchestration = resolution.Orchestration;

        // Both tasks are created back-to-back with no await between them, so they are either both set or both null; a
        // throw before the package build leaves both null and the finally has nothing to drain.
        Task? pumpTask = null;
        Task? runTask = null;

        try
        {
            // Tools are offered only when the client asked, the node tool engine is enabled and the model advertises
            // the capability; a bound definition then narrows the offer to its allowed set.
            var enableTools = await _runtimeSettings.GetEnableToolsAsync(cancellationToken);
            var offerTools = useLocalTools && enableTools && resolution.SupportsTools;
            var allowedTools = offerTools
                ? await ResolveAllowedToolsAsync(activeModel, resolution, cancellationToken)
                : null;

            // An Orchestrator whose orchestration did not compile reruns as a lone single agent, so emit ONE notice
            // naming the typed reason; a Single-kind agent (NotOrchestrated) has none, so the common path is silent.
            if (resolution.OrchestrationOutcome.DegradationNotice is { } orchestrationDegradedMessage)
            {
                await _eventDispatcher.ReportTurnNoticeAsync(new TurnNoticePayload
                {
                    InvocationId = requestId,
                    Kind = TurnNoticeKind.OrchestrationDegraded,
                    Message = orchestrationDegradedMessage,
                    Detail = resolution.OrchestrationOutcome.Reason.ToString()
                });
            }

            // A regenerated plain-chat turn honors the same opt-in grounding and cloud-egress gate as a send, so a
            // rerun does not lose its sources strip. Agent mode grounds through the gated tool instead, never inline.
            var knowledge = useKnowledgeBase && !offerTools
                ? await GroundOnKnowledgeBaseAsync(conversation, original, resolution, requestId, runCancellation.Token, cancellationToken)
                : null;

            var package = _runtimePackageBuilder.Build(new LocalChatRuntimePackageRequest
            {
                InvocationId = requestId,
                ConversationId = conversationId,
                ResolvedSystemPrompt = resolved?.ResolvedSystemPrompt ?? await LoadResolvedSystemPromptAsync(_localChatOptions.Value),
                ConversationContext = BuildRegenerationContext(conversation, original, selectedPath, knowledge?.Message),
                ModelProfile = resolution.EffectiveModel,
                AgentDefinitionVersion = resolved?.AgentDefinitionVersion ?? AgentDefinitionVersion,
                ClientNodeId = LocalChatLoopbackDefaults.ClientNodeId,
                AllowedTools = allowedTools,
                Timeouts = new TimeoutSettings
                {
                    InvocationTimeoutSeconds = runtimeNodeSettings.MaxMessageRequestTimeoutSeconds
                },
                ReasoningEffort = resolved?.ReasoningEffort ?? reasoningEffort,
                OrchestrationSpec = orchestration?.Spec,
                SupportsThinking = resolution.SupportsThinking,
                // Per-turn sampling overrides, carried exactly as the send path carries them so a regenerated turn
                // reruns under the same knobs the original send used. Null keeps the package byte-identical to today.
                SamplingOptions = samplingOptions,
                Skills = resolved?.Skills,
                CustomTools = resolved?.CustomTools,
                ReasoningBudgetEnforceable = resolution.ReasoningBudgetEnforceable,
                // Carried exactly as the send path carries it, so a regenerated turn sees the same tool array.
                DisableToolRelevanceFilter = resolved?.DisableToolRelevanceFilter ?? false,
                // Carried exactly as the send path carries it: false = pinned, so no dispatcher model swap.
                AllowAutoModelSwap = resolution.AllowAutoModelSwap
            });

            // The post-run adaptive-memory hook fires once on a Completed or Failed terminal, and ONLY when the resolved
            // agent has the playbook enabled AND opts into extraction, so a retrieval-only agent mines nothing new.
            var memoryHook = resolution.Resolved is { PlaybookEnabled: true, MemoryExtractionEnabled: true } memoryAgent
                ? ChatMemoryExtractionHook.Build(_memoryExtractionDispatcher,
                    memoryAgent,
                    conversation.ConversationId,
                    conversation.MemoryExcluded,
                    package,
                    resolution.EffectiveModel,
                    () => CollectUserTurns(conversation, original, selectedPath))
                : null;
            var onTerminal = ChatCompactionTriggerHook.Compose(_logger,
                memoryHook,
                ChatCompactionTriggerHook.Build(_conversationMaintenanceDispatcher, conversation.ConversationId));

            pumpTask = _invocationStatePump.PumpAsync(stateChannel.Reader,
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

            // Ownership is established: the pump and runner drive every row to the runner's true terminal, so the
            // pre-ownership guard stands down — a disconnect from here on must NOT terminalize.
            preOwnershipGuard.OwnershipEstablished();

            // The client cancellationToken stops THIS forwarding loop only. The run and pump keep going on
            // runCancellation.Token so the runner reaches its real terminal and the pump persists it.
            await foreach (var streamEvent in eventSink.ReadAllAsync(cancellationToken))
            {
                yield return streamEvent;
            }
        }
        finally
        {
            // Detach FIRST, before draining below: every producer write becomes a no-op so the abandoned stream retains
            // nothing. It deliberately does not COMPLETE the queue — the pump reads a write fault as a persistence fault.
            eventSink.Detach();

            // Never cancel runCancellation on a disconnect: both tasks drain to the runner's true terminal so
            // persistence follows the runner's lifecycle, not the connection's.
            if (pumpTask is not null && runTask is not null)
            {
                await DrainRunAsync(pumpTask, runTask, runCancellation, requestId);
            }
        }
    }

    /// <summary>
    ///     Reads the conversation this regenerate reruns, settles which variant branch shapes its history and locates
    ///     the assistant turn being replaced, mirroring <c>NodeChatStreamService.LoadTurnAsync</c>.
    /// </summary>
    private async Task<RegenerationTurnLoad> LoadRegenerationTurnAsync(Guid conversationId,
        Guid originalMessageId,
        IReadOnlyDictionary<Guid, Guid>? requestedSelectedPath,
        CancellationToken cancellationToken)
    {
        // Reject regeneration on a remote-origin (view-only) conversation before any persistence. The guard is
        // authoritative; throwing here propagates to the hub caller.
        await _mutationGuard.EnsureMutableAsync(conversationId, cancellationToken);

        // A request-supplied selection is persisted BEFORE the read, because that write also CLEARS the stored
        // compaction synopsis: reading first would splice a stale summary in and drop the messages it claims to cover.
        var persistedSelectedPath = requestedSelectedPath is not null
            ? await _persistence.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest
            {
                ConversationId = conversationId,
                SelectedPath = requestedSelectedPath,
                UpdatedAtUtc = NowUnixMilliseconds()
            }, cancellationToken)
            : null;

        var conversation = await _persistence.GetConversationAsync(conversationId, cancellationToken)
                           ?? throw new NodeChatConversationNotFoundException(conversationId);

        var original = conversation.Messages.FirstOrDefault(message => message.MessageId == originalMessageId)
                       ?? throw new NodeChatMessageNotFoundException(originalMessageId);

        // Same precedence as the send path: a request-supplied selection is persisted and used; otherwise the
        // already-persisted conversation selection drives the pre-cutoff context.
        return new RegenerationTurnLoad
        {
            Conversation = conversation,
            SelectedPath = persistedSelectedPath ?? conversation.SelectedPath,
            Original = original
        };
    }

    // Reuses the backend mint for the sibling placeholder (pending, shared variant_group_id, parent copied from the
    // original) — never an in-place overwrite. It carries the resolved agent's attribution from the pending frame on.
    private async Task<NodeChatPersistedMessageDto> MintVariantAsync(Guid conversationId,
        Guid originalMessageId,
        Guid newMessageId,
        Guid requestId,
        long startedAtUtc,
        ChatTurnResolution resolution,
        string? reasoningEffort,
        CancellationToken cancellationToken)
    {
        var variant = await _persistence.CreateMessageVariantAsync(new NodeChatCreateMessageVariantRequest
                          {
                              ConversationId = conversationId,
                              OriginalMessageId = originalMessageId,
                              NewMessageId = newMessageId,
                              RequestId = requestId,
                              CreatedAtUtc = startedAtUtc,
                              // Stamped with the model that will actually rerun, not the raw original model, so the
                              // variant's attribution matches the rerun.
                              Model = resolution.EffectiveModel,
                              AgentDefinitionId = resolution.Resolved?.AgentDefinitionId,
                              AgentName = resolution.Resolved?.AgentName,
                              // The effort that actually drives this variant: an agent's pin wins over the request's
                              // selection, the same precedence as the rerun's package, and it survives reload.
                              ReasoningEffort = resolution.Resolved?.ReasoningEffort ?? reasoningEffort
                          },
                          cancellationToken)
                      ?? throw new NodeChatMessageNotFoundException(originalMessageId);

        return variant.Variant;
    }

    /// <summary>
    ///     The tools that travel in the runtime package for a turn that offers them.
    /// </summary>
    /// <remarks>
    ///     A bound definition's AllowedTools already ran through the node approval policy in
    ///     <see cref="ChatTurnResolver" />, custom tools merged there. The unbound fallback builds the raw offer here
    ///     and applies the SAME tighten-only policy to avoid a bypass; the Permissive floor is identity, so an
    ///     unconfigured node stays byte-identical to the raw catalog offer. A custom tool on that agentless path is
    ///     not session-approvable and re-prompts each time.
    /// </remarks>
    private async Task<IReadOnlyList<AllowedToolDto>> ResolveAllowedToolsAsync(string? activeModel, ChatTurnResolution resolution, CancellationToken cancellationToken)
    {
        if (resolution.Resolved?.AllowedTools is { } resolvedAllowedTools)
        {
            return resolvedAllowedTools;
        }

        var fallbackOffer = await _localToolOfferProvider.GetOfferedToolsAsync(activeModel, resolution.EffectiveModelIsCloud, cancellationToken);
        return
        [
            .. fallbackOffer.Select(tool => tool with
            {
                RequiresApproval = _toolApprovalPolicy.RequiresApproval(tool.Name, tool.Category, tool.RequiresApproval)
            })
        ];
    }

    /// <summary>
    ///     The knowledge-base grounding for a regenerated PLAIN-CHAT turn, composed by the shared
    ///     <see cref="IChatTurnContextBuilder" /> so a rerun grounds byte-identically to the original send.
    /// </summary>
    /// <remarks>
    ///     The retrieval query is the user turn the regenerate re-answers, on the same cutoff anchor as the
    ///     regeneration context. Returns <see langword="null" /> when the egress gate withholds grounding, when there
    ///     is no preceding user turn, or when retrieval produced nothing.
    /// </remarks>
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
        var knowledgeAllowed = !turnReachesCloud || _knowledgeOptions.Value.AllowCloudModelAccess;
        if (!knowledgeAllowed)
        {
            // Name the model the notice is about: the effective cloud model when that is what reaches the cloud,
            // otherwise the cloud participant whose presence forced the withhold on an otherwise-local root.
            var cloudModelForNotice = resolution.EffectiveModelIsCloud
                ? resolution.EffectiveModel
                : resolution.Orchestration?.FirstCloudParticipantModel ?? resolution.EffectiveModel;
            await ReportKnowledgeWithheldAsync(cloudModelForNotice, requestId, cancellationToken);
            return null;
        }

        var retrievalQuery = ResolvePrecedingUserTurnContent(conversation, original);
        return string.IsNullOrWhiteSpace(retrievalQuery)
            ? null
            : await _turnContextBuilder.BuildKnowledgeContextAsync(retrievalQuery, isRegeneratedTurn: true, runCancellationToken);
    }

    // Drains the run after the SSE consumer is gone. The pump is observed FIRST: a persistence fault faults here, so
    // the run is cancelled rather than left generating output that can no longer be persisted.
    private async Task DrainRunAsync(Task pumpTask, Task runTask, CancellationTokenSource runCancellation, Guid requestId)
    {
        try
        {
            await pumpTask;
        }
        catch (OperationCanceledException)
        {
            // The cancelled/interrupted terminal is persisted by the pump.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Local node chat regeneration pump faulted; cancelling the run. RequestId={RequestId}", requestId);
            await runCancellation.CancelAsync();
        }

        try
        {
            await runTask;
        }
        catch (OperationCanceledException)
        {
            // The runner unwound on cancellation; its terminal is persisted by the pump.
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Local node chat regeneration run completed with an exception after teardown. RequestId={RequestId}", requestId);
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
        // Queue behind any in-flight invocation under the shared lease rather than failing the turn. A cancel while
        // still queued aborts the wait and the run is terminalized as cancelled below.
        IAsyncDisposable? lease = null;

        try
        {
            lease = await _eventDispatcher.ReportInvocationAssignedAsync(package, cancellationToken);

            var streamingMessage = await _persistence.MarkAssistantStreamingAsync(correlation, NowUnixMilliseconds(), cancellationToken);
            if (!string.Equals(streamingMessage.Status, NodeChatMessageStatusValues.Streaming, StringComparison.Ordinal))
            {
                // The variant was finalized (cancelled) before streaming could start. Do not stream into a terminal
                // message or run the model: return so the finally completes the state channel and the pump terminalizes.
                return;
            }

            await eventSink.WriteAsync(ToMessageEvent(ChatStreamEventTypes.AssistantStreaming, correlation, streamingMessage, sequence.Next(),
                    invocationTimeoutSeconds: package.Timeouts.InvocationTimeoutSeconds),
                cancellationToken);

            // Symmetric with the send path: a regenerate of a "Local runtime default" turn that resolved no installed
            // GGUF chat model fails BEFORE any provider invocation with the dedicated ModelNotInstalled category.
            if (requiresInstalledChatModel)
            {
                throw new NoChatModelInstalledException();
            }

            var context = InvocationExecutionContext.CreatePlain(package, messageId);
            await _invocationRunner.RunAsync(context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await _eventDispatcher.ReportInvocationFailedAsync(requestId,
                PreRunCancelledMessage,
                FailureCategory.Cancelled);
        }
        catch (NoChatModelInstalledException exception)
        {
            // Classified separately so the terminal SSE carries ModelNotInstalled (not Unexpected/ProviderUnreachable).
            _logger.LogWarning(exception, "Local node chat regeneration had no installed GGUF chat model for the local default. RequestId={RequestId}", requestId);
            await _eventDispatcher.ReportInvocationFailedAsync(requestId,
                exception.Message,
                FailureCategory.ModelNotInstalled);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Local node chat regeneration failed. RequestId={RequestId}", requestId);
            await _eventDispatcher.ReportInvocationFailedAsync(requestId,
                "local-chat-regeneration-failed",
                FailureCategory.Unexpected);
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }

            stateWriter.TryComplete();
        }
    }

    /// <summary>
    ///     Builds the regeneration context: every completed message UP TO AND INCLUDING the USER turn that precedes
    ///     the turn being regenerated, excluding the original assistant answer and any sibling variants.
    /// </summary>
    /// <remarks>
    ///     A variant's parent is the prior assistant, not the user turn, so a parent walk cannot reach that turn.
    ///     The cutoff is instead the latest USER turn strictly before the EARLIEST member of the original's variant
    ///     group, so every member of that group sorts at or after it and is excluded. With no preceding user turn,
    ///     the context is everything strictly before the earliest group member.
    /// </remarks>
    /// <param name="applyCompaction">
    ///     False only for memory extraction, which mines REAL user turns: no synopsis, no prompt-only unanswered notice.
    /// </param>
    private static IReadOnlyList<ConversationMessageDto> BuildRegenerationContext(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto original,
        IReadOnlyDictionary<Guid, Guid>? selectedPath,
        ConversationMessageDto? knowledgeContext = null,
        bool applyCompaction = true)
    {
        // The cutoff, the compaction splice and the ordering all run in ANCHOR space (each group's earliest member
        // sequence), never on a sibling's own sequence, or a late-regenerated early turn would drop out of context.
        var anchorSequence = SelectedPathResolver.CreateAnchorResolver(conversation.Messages);
        var cutoffSequence = ResolvePrecedingUserTurnCutoff(conversation, original, anchorSequence);

        // A prior turn before the cutoff may itself have variants, collapsed here to the selected path. The group
        // being regenerated already sorts at or after the cutoff, so the sequence filter excludes it either way.
        var selected = SelectedPathResolver.Resolve(conversation.Messages, selectedPath);
        // Earlier failed requests are marked like the send path marks them. The turn at the cutoff is the one this
        // rerun answers, so it never is, even when the answer being replaced is the one that failed.
        var unanswered = applyCompaction
            ? ConversationContextBuilder.FindUnansweredUserTurns(selected.Where(message => anchorSequence(message) < cutoffSequence), anchorSequence)
            : [];

        // The synthetic context messages — knowledge grounding, then the compaction synopsis — take the first slots so
        // the model reads them before the history, which shifts down by their count. Empty on a plain rerun.
        var leadingContext = new List<ConversationMessageDto>(capacity: 2);
        if (knowledgeContext is not null)
        {
            leadingContext.Add(knowledgeContext with
            {
                SortOrder = 0
            });
        }

        // Non-destructive compaction: the synopsis replaces the messages it covers, but only while the covered sequence
        // sits BELOW the cutoff; the state is rendered as of the cutoff too, or the answer being replaced would steer its rerun.
        if (applyCompaction && CompactionContextResolver.Resolve(conversation, leadingContext.Count, stateAsOfSequence: cutoffSequence) is { } compaction &&
            compaction.CoveredSequence < cutoffSequence)
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
                           Content = ConversationContextBuilder.WithUnansweredNotice(message, unanswered),
                           Thinking = message.Reasoning,
                           ModelUsed = message.Model,
                           SortOrder = index + leadingContext.Count
                       })
                       .ToList();

        return leadingContext.Count == 0 ? messages : [.. leadingContext, .. messages];
    }

    /// <summary>
    ///     Resolves the inclusive cutoff sequence for the regeneration context: the latest USER turn strictly before
    ///     the earliest member of the original's variant group.
    /// </summary>
    /// <remarks>
    ///     The group spans the original answer and every sibling variant, so anchoring on its earliest member keeps
    ///     all of them out of context whichever member is being regenerated. With no preceding user turn, the slot
    ///     before the earliest group member is returned, so the context is everything that came before — never the
    ///     answer being replaced.
    /// </remarks>
    private static int ResolvePrecedingUserTurnCutoff(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto original,
        Func<NodeChatPersistedMessageDto, int> anchorSequence)
    {
        var precedingUserTurn = ResolvePrecedingUserTurn(conversation, original, anchorSequence);
        return precedingUserTurn is not null ? anchorSequence(precedingUserTurn) : anchorSequence(original) - 1;
    }

    /// <summary>
    ///     The latest USER turn anchored strictly before the original's variant group.
    /// </summary>
    /// <remarks>
    ///     The group's anchor IS the earliest member's sequence
    ///     (<see cref="SelectedPathResolver.CreateAnchorResolver{TMessage}" />), so the original answer and each
    ///     sibling variant anchor at or after it and are excluded whichever member is being regenerated.
    /// </remarks>
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
    ///     Collects the user-role turns for extraction from the regeneration context: pre-cutoff, selected-path
    ///     collapsed, excluding the original answer and its sibling variants.
    /// </summary>
    /// <remarks>
    ///     The agent's regenerated answer is supplied separately as the run's <c>AssistantResponse</c>. Content is
    ///     held only for the in-scope model call and dedup, never persisted here.
    /// </remarks>
    private static IReadOnlyList<MemoryExtractionTurn> CollectUserTurns(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto original,
        IReadOnlyDictionary<Guid, Guid>? selectedPath)
    {
        return BuildRegenerationContext(conversation, original, selectedPath, knowledgeContext: null, applyCompaction: false)
               .Where(static message => message.Role == MessageRole.User && !string.IsNullOrWhiteSpace(message.Content))
               .Select(static message => new MemoryExtractionTurn
               {
                   Content = message.Content
               })
               .ToArray();
    }

    // The same resource AgentInstructionProvider.GetBaseScaffold reads, kept as a local literal to avoid a DI
    // dependency on IAgentInstructionProvider in this already-large constructor.
    private const string BaseScaffoldResourceName = "XE_Local_AI_Engine.AI.Agent.Instructions.BaseScaffold.txt";

    /// <summary>
    ///     Reads the embedded chat prompt for the null-definition fallback and prepends the same versioned base
    ///     scaffold a resolved agent gets, so an unbound regenerate is covered identically to a bound one.
    /// </summary>
    private static async Task<string> LoadResolvedSystemPromptAsync(LocalChatAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.InstructionsResource))
        {
            throw new ArgumentException("Instructions resource must be provided.", nameof(options));
        }

        var persona = await LoadEmbeddedResourceAsync(options.InstructionsResource);
        var scaffold = await LoadEmbeddedResourceAsync(BaseScaffoldResourceName);
        return string.IsNullOrWhiteSpace(scaffold) ? persona : $"{scaffold.TrimEnd()}\n\n{persona}";
    }

    // Reads an embedded manifest resource: the bytes are already in the loaded assembly image, so there is no I/O to
    // abandon. CancellationToken.None is the analyzers' documented "intentionally not propagating" opt-out.
    private static async Task<string> LoadEmbeddedResourceAsync(string resourceName)
    {
        var assembly = typeof(LocalChatAgentOptions).Assembly;
        await using var stream = assembly.GetManifestResourceStream(resourceName)
                                 ?? throw new InvalidOperationException($"Embedded instructions resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(CancellationToken.None);
    }

    /// <summary>
    ///     Derives the offer-time active model and the effective agent head for a regenerate, then defers to the
    ///     shared <see cref="ChatTurnResolver" /> for capability, definition and orchestration resolution.
    /// </summary>
    /// <remarks>
    ///     The effective-agent precedence reuses the ORIGINAL turn's recorded agent so a rerun stays on the same
    ///     persona: <c>original.AgentDefinitionId ?? conversation.AgentDefinitionId ?? (memoized) Default Assistant
    ///     id</c>. The attribution name is re-resolved so a rename is picked up; a deleted agent resolves to null and
    ///     the variant falls back to the original's stored name. The retrieval query is the preceding user turn.
    /// </remarks>
    private async Task<ChatTurnResolution> ResolveTurnAsync(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto original,
        CancellationToken cancellationToken)
    {
        // An explicit original-turn model is an operator pick: reused unchanged, and it wins over a bound agent's
        // pinned ModelProfile for both the rerun and the attribution. A blank one re-resolves to an installed GGUF.
        string? activeModel;
        var requiresInstalledChatModel = false;
        var userPickedConcreteModel = !string.IsNullOrWhiteSpace(original.Model);
        if (userPickedConcreteModel)
        {
            activeModel = original.Model;
        }
        else
        {
            var nodeSettings = await _nodeSettingsStore.LoadAsync(cancellationToken);
            activeModel = await _localDefaultChatModelResolver.ResolveAsync(nodeSettings.DefaultModelName, cancellationToken);
            requiresInstalledChatModel = activeModel is null;
        }

        // A regenerate reuses the ORIGINAL turn's agent so the rerun stays on the same persona; fall back to the
        // conversation binding, then the seeded Default Assistant (process-memoized id).
        var effectiveAgentId = original.AgentDefinitionId
                               ?? conversation.AgentDefinitionId
                               ?? await _defaultAgentProvider.GetDefaultAgentIdAsync(cancellationToken);

        var retrievalQuery = ResolvePrecedingUserTurnContent(conversation, original);

        return await _turnResolver.ResolveAsync(activeModel, requiresInstalledChatModel, effectiveAgentId, retrievalQuery, userPickedConcreteModel, cancellationToken);
    }

    /// <summary>
    ///     The content of the latest USER turn strictly before the original's variant group — the question the
    ///     regenerate re-answers, used as the relevance-retrieval query.
    /// </summary>
    /// <remarks>
    ///     It uses the same cutoff anchor as <see cref="ResolvePrecedingUserTurnCutoff" /> and returns
    ///     <see langword="null" /> when no such user turn exists, so the resolver falls back to the static prepend.
    /// </remarks>
    private static string? ResolvePrecedingUserTurnContent(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto original) =>
        ResolvePrecedingUserTurn(conversation, original, SelectedPathResolver.CreateAnchorResolver(conversation.Messages))?.Content;

    // Emits the KnowledgeWithheld notice when a regenerated plain-chat turn opted into grounding but a cloud effective
    // model would have received it without the operator's opt-in. The rerun still runs, just without that context.
    private async Task ReportKnowledgeWithheldAsync(string? effectiveModel, Guid requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _eventDispatcher.ReportTurnNoticeAsync(new TurnNoticePayload
        {
            InvocationId = requestId,
            Kind = TurnNoticeKind.KnowledgeWithheld,
            Message =
                "Your knowledge base was not searched for this message because it is handled by a cloud model. Enable cloud data access for this node to allow knowledge-base grounding to reach a cloud model.",
            Detail = effectiveModel
        });
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
        return _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }

    // The conversation this regenerate reruns, the variant branch that shapes its history, and the assistant turn
    // being replaced, which the cutoff anchors on.
    private sealed record RegenerationTurnLoad
    {
        public required NodeChatConversationDto Conversation { get; init; }

        public required IReadOnlyDictionary<Guid, Guid>? SelectedPath { get; init; }

        public required NodeChatPersistedMessageDto Original { get; init; }
    }
}
