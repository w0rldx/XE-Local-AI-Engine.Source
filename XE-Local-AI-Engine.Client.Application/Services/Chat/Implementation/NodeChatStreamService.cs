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
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Coder.Tools;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

public sealed class NodeChatStreamService : INodeChatStreamService
{
    private const int AgentDefinitionVersion = 1;

    // A cancel landing before the invocation starts: still waiting for the collision-queue lease, or between acquiring
    // it and the Streaming transition. Distinct from the runner's terminals so a queue stop never reads as a timeout.
    private const string PreRunCancelledMessage = "Stopped before the response started (cancelled while queued).";

    // The tools whose presence in an offer means the agent can read files through the AgentHome sandbox. When any is
    // offered AND the conversation has attachments, the sandbox is re-staged with them before the tool loop runs.
    private static readonly HashSet<string> AgentHomeCapableToolNames = new(StringComparer.Ordinal)
    {
        CoderToolDefinition.ListFilesToolName,
        CoderToolDefinition.ReadFileToolName,
        CoderToolDefinition.SearchTextToolName,
        AgentHomeToolDefinition.ToolName
    };

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
    private readonly IConversationSandboxStager _conversationSandboxStager;
    private readonly IOptions<KnowledgeBaseOptions> _knowledgeOptions;
    private readonly IOptions<ChatStreamBudgetOptions> _streamBudgetOptions;
    private readonly TimeProvider _timeProvider;
    private readonly IToolApprovalPolicy _toolApprovalPolicy;
    private readonly ILogger<NodeChatStreamService> _logger;

    public NodeChatStreamService(
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
        IConversationMaintenanceDispatcher conversationMaintenanceDispatcher,
        IChatTurnContextBuilder turnContextBuilder,
        IConversationSandboxStager conversationSandboxStager,
        IOptions<KnowledgeBaseOptions> knowledgeOptions,
        IOptions<ChatStreamBudgetOptions> streamBudgetOptions,
        TimeProvider timeProvider,
        IToolApprovalPolicy toolApprovalPolicy,
        ILogger<NodeChatStreamService> logger)
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
        _conversationSandboxStager = conversationSandboxStager;
        _knowledgeOptions = knowledgeOptions;
        _streamBudgetOptions = streamBudgetOptions;
        _timeProvider = timeProvider;
        _toolApprovalPolicy = toolApprovalPolicy;
        _logger = logger;
    }

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

        var turn = await LoadTurnAsync(request, cancellationToken);
        var conversation = turn.Conversation;
        var selectedPath = turn.SelectedPath;

        var trimmedContent = request.Content.Trim();
        var userMessageId = request.UserMessageId.GetValueOrDefault(Guid.NewGuid());
        var assistantMessageId = request.MessageId.GetValueOrDefault(Guid.NewGuid());
        var requestId = request.RequestId.GetValueOrDefault(Guid.NewGuid());
        var correlation = new NodeChatMessageCorrelation { ConversationId = request.ConversationId, MessageId = assistantMessageId, RequestId = requestId };
        var sequence = new NodeChatStreamSequence();
        var startedAtUtc = NowUnixMilliseconds();

        var userMessage = await _persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest { ConversationId = request.ConversationId, MessageId = userMessageId, Content = trimmedContent, CreatedAtUtc = startedAtUtc },
            cancellationToken);
        yield return ToMessageEvent(ChatStreamEventTypes.UserMessagePersisted, correlation, userMessage, sequence.Next());

        // Resolved BEFORE the assistant placeholder is minted, because the placeholder is stamped with the resolved
        // agent's id and display name. It reads only conversation, content and path, so the emitted order is unchanged.
        var resolution = await ResolveTurnAsync(request, conversation, activeModelOverride: null, trimmedContent, cancellationToken);

        // GRAPH-C4-2 is asked of the OFFER this turn really hands the model, resolved once here and reused verbatim
        // below; only an armed turn resolves early, so a refusal leaves no stranded assistant row.
        ChatToolOffer? declaredWriteOffer = null;
        if (request.RefuseUndeclaredWrites)
        {
            declaredWriteOffer = await ResolveToolOfferAsync(request, resolution, cancellationToken);
            if (WorkSessionWriteDeclarationGuard.Refuse(declaredWriteOffer.AllowedTools, resolution.Resolved is not null) is { } undeclaredWrite)
            {
                throw new WorkSessionUndeclaredWriteException(undeclaredWrite);
            }
        }

        var assistantPlaceholder = await PersistAssistantPlaceholderAsync(request, resolution, assistantMessageId, requestId, cancellationToken);

        // The row is Pending but run ownership (pump, runner, their finally) is not wired yet, so a disconnect in this
        // window would leave it Pending until the restart reaper. The guard terminalizes it Interrupted, then no-ops.
        await using var preOwnershipGuard = new PreOwnershipTerminalizationGuard(_persistence, correlation, _timeProvider, _logger);
        yield return ToMessageEvent(ChatStreamEventTypes.AssistantPending, correlation, assistantPlaceholder, sequence.Next());

        // Only the invocation timeout is operator-controlled, and at the TimeoutSettings default the package and its
        // config hash stay byte-identical. Loaded here because the queued and streaming events stamp this ceiling.
        var runtimeNodeSettings = await _nodeSettingsStore.LoadAsync(cancellationToken);

        // The turn stays Queued until RunInvocationAsync acquires the collision-queue lease, so a turn waiting behind
        // another invocation reads as "queued" rather than prematurely "streaming".
        var queuedMessage = await _persistence.MarkAssistantQueuedAsync(correlation, NowUnixMilliseconds(), cancellationToken);
        if (!string.Equals(queuedMessage.Status, NodeChatMessageStatusValues.Queued, StringComparison.Ordinal))
        {
            // The queued mark was rejected because a cancel raced ahead of run ownership and the row is already terminal.
            // Surface the terminal the row holds and abort: never wire a pump or runner for a finalized turn.
            yield return ToMessageEvent(ChatStreamEventMapper.TerminalEventType(queuedMessage.Status), correlation, queuedMessage, sequence.Next());
            yield break;
        }

        yield return ToMessageEvent(ChatStreamEventTypes.AssistantQueued, correlation, queuedMessage, sequence.Next(),
            invocationTimeoutSeconds: runtimeNodeSettings.MaxMessageRequestTimeoutSeconds);

        // The run and persistence lifecycle belongs to the runner, not the client connection: runCancellation is
        // deliberately NOT linked to cancellationToken and is tripped only by a user cancel through the registry.
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
        // Six producers write this sink concurrently: pump deltas and terminals, the run transition, the tool-call
        // lifecycle, turn notices, approval requests and user questions. It is BOUNDED and never makes one wait.
        var eventSink = new ChatStreamEventSink(correlation, sequence, _streamBudgetOptions.Value, _timeProvider);

        // Accumulates the ordered reasoning/tool interleave so the terminal persist can write parts[], the reload render
        // source. Fed by BOTH producers: the forwarder's tool/notice handlers and the pump loop's reasoning deltas.
        var parts = new NodeChatPartAccumulator();

        // Subscribed before pre-run notice production so the cloud-withholding notices reach the stream. The scope also
        // covers every pre-ownership exit, so a staging or package-construction failure cannot leak handlers.
        using var eventSubscription = new ChatStreamEventForwarder(_eventDispatcher, correlation, requestId, stateChannel.Writer, eventSink, sequence, parts, _timeProvider);

        // The armed turn's offer, already settled above and reused verbatim so the rule judged the same list the package
        // carries; every other turn resolves it right here, exactly where it always did.
        var toolOffer = declaredWriteOffer ?? await ResolveToolOfferAsync(request, resolution, cancellationToken);
        var offerTools = toolOffer.OfferTools;
        // The ask_user withdrawal applies to the single FINAL list, so it holds whichever resolver produced this offer.
        // Orchestration participants' own lists ride the compiled spec instead and are filtered where it is built.
        var allowedTools = request.SuppressAskUser ? AskUserToolOffer.Withdraw(toolOffer.AllowedTools) : toolOffer.AllowedTools;

        var attachmentsAllowed = AreAttachmentsAllowed(resolution);
        await ReportPreRunNoticesAsync(request, resolution, offerTools, attachmentsAllowed, requestId, cancellationToken);

        // Agent mode: when the offer includes the sandbox file tools, re-stage the sandbox with THIS conversation's
        // attachments BEFORE building the turn context, so the file tools see them under attachments/.
        var isAgentHomeTurn = offerTools && OffersAgentHomeTools(allowedTools);
        var staging = isAgentHomeTurn && attachmentsAllowed
            ? await StageConversationAttachmentsAsync(request.ConversationId, runCancellation.Token)
            : null;

        await using var sandboxPreparationLease = staging?.Preparation;
        if (staging?.Error is { } sandboxPreparationError)
        {
            var failedMessage = await TerminalizeAssistantFailureAsync(correlation, sandboxPreparationError);
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
            cancellationToken);

        var package = await BuildRuntimePackageAsync(request,
                resolution,
                ConversationContextBuilder.Build(conversation, userMessage, selectedPath, turnContext.Attachment, turnContext.Image, turnContext.Knowledge),
                allowedTools,
                runtimeNodeSettings.MaxMessageRequestTimeoutSeconds,
                requestId);
        var preRunDurationMs = Stopwatch.GetElapsedTime(harnessStartedTimestamp).TotalMilliseconds;

        // A work-session step is bounded by ConversationStepContextBound before it sends, so only a chat turn auto-compacts.
        var onTerminal = ChatCompactionTriggerHook.Compose(_logger,
            BuildMemoryExtractionHook(resolution, conversation, userMessage, selectedPath, package),
            request.IsWorkSessionTurn ? null : ChatCompactionTriggerHook.Build(_conversationMaintenanceDispatcher, conversation.ConversationId));

        Task pumpTask;
        Task runTask;
        using var invocationScope = staging?.Preparation?.EnterInvocationScope();
        pumpTask = _invocationStatePump.PumpAsync(stateChannel.Reader,
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
        // The sandbox is touched only by the run's tool calls, so the workspace goes back when the run ends rather than
        // when this stream unwinds; the lease never queues, so holding it longer fails the next turn as "busy".
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

        // Ownership is established: the pump and runner drive every row to the runner's true terminal, so the
        // pre-ownership guard stands down — a disconnect from here on must NOT terminalize.
        preOwnershipGuard.OwnershipEstablished();

        try
        {
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

            // Never cancel runCancellation on a disconnect: both tasks drain to the runner's true terminal, so the run
            // holds the collision slot until it ends. Freeing the slot here resurrects the interrupted-terminal bug.
            await DrainRunAsync(pumpTask, runTask, runCancellation, eventSubscription, requestId);
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
        // Queue behind any in-flight invocation rather than failing the turn. The lease holds the shared slot; a cancel
        // while still queued aborts the wait and terminalizes the turn as cancelled below.
        IAsyncDisposable? lease = null;

        try
        {
            var queueStartedTimestamp = Stopwatch.GetTimestamp();
            lease = await _eventDispatcher.ReportInvocationAssignedAsync(package, cancellationToken);
            var queueDurationMs = Stopwatch.GetElapsedTime(queueStartedTimestamp).TotalMilliseconds;

            // The lease is held => the invocation is actually starting. Transition Queued -> Streaming and emit
            // the streaming event so the client leaves the queued state.
            var streamingMessage = await _persistence.MarkAssistantStreamingAsync(correlation, NowUnixMilliseconds(), cancellationToken);
            if (!string.Equals(streamingMessage.Status, NodeChatMessageStatusValues.Streaming, StringComparison.Ordinal))
            {
                // The row was finalized (cancelled) before streaming could start. Do not stream into a terminal message or
                // run the model: return so the finally completes the state channel and the pump terminalizes from the row.
                return;
            }

            await eventSink.WriteAsync(ToMessageEvent(ChatStreamEventTypes.AssistantStreaming, correlation, streamingMessage, sequence.Next(),
                                   invocationTimeoutSeconds: package.Timeouts.InvocationTimeoutSeconds),
                               cancellationToken);

            // A "Local runtime default" send that resolved no installed GGUF chat model fails BEFORE any provider call,
            // so the client sees an actionable "pull a model" terminal rather than the stale-id "Provider unreachable.".
            if (requiresInstalledChatModel)
            {
                throw new NoChatModelInstalledException();
            }

            var context = InvocationExecutionContext.CreatePlain(package,
                messageId,
                harnessStartedTimestamp,
                preRunDurationMs,
                queueDurationMs);
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
            // Classified separately from the generic catch so the terminal SSE carries ModelNotInstalled (not
            // Unexpected/ProviderUnreachable) and the message is the actionable, path-free constant.
            _logger.LogWarning(exception, "Local node chat stream had no installed GGUF chat model for the local default. RequestId={RequestId}", requestId);
            await _eventDispatcher.ReportInvocationFailedAsync(requestId,
                exception.Message,
                FailureCategory.ModelNotInstalled);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Local node chat stream failed. RequestId={RequestId}", requestId);
            await _eventDispatcher.ReportInvocationFailedAsync(requestId,
                "local-chat-stream-failed",
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
    ///     Collects the user turns for extraction: the prior completed user turns on the selected path plus the
    ///     just-sent one, ordered.
    /// </summary>
    /// <remarks>
    ///     Assistant turns are excluded — the agent's own answer is supplied separately as the run's
    ///     <c>AssistantResponse</c>. Content is held only for the in-scope model call and dedup, never persisted here.
    /// </remarks>
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
               .Select(static message => new MemoryExtractionTurn { Content = message.Content })
               .ToArray();
    }

    // Emits the AttachmentsWithheld notice when a cloud effective model would have received attachment content without
    // the operator's opt-in, but only when there is something to withhold, so a plain cloud chat stays silent.
    private async Task ReportAttachmentsWithheldIfPresentAsync(NodeChatStreamRequest request,
        string? effectiveModel,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var hasAttachments = await _turnContextBuilder.HasAttachmentContentAsync(request.ConversationId, request.AttachmentFileIds, cancellationToken);
        if (!hasAttachments)
        {
            return;
        }

        await _eventDispatcher.ReportTurnNoticeAsync(new TurnNoticePayload
                             {
                                 InvocationId = requestId,
                                 Kind = TurnNoticeKind.AttachmentsWithheld,
                                 Message =
                                     "Your uploaded files were not shared with the cloud model handling this message. Enable cloud data access for this node to allow attachments and file tools to reach a cloud model.",
                                 Detail = effectiveModel
                             });
    }

    // Emits the KnowledgeWithheld notice when a plain-chat turn opted into grounding but a cloud effective model would
    // have received it without the operator's opt-in. The turn still runs, just without knowledge-base context.
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

    /// <summary>
    ///     Reads the conversation this send belongs to and settles which variant branch shapes its history.
    /// </summary>
    /// <remarks>
    ///     A selection map on the request is the authoritative, just-clicked path and is persisted BEFORE the read,
    ///     because that write also CLEARS the stored compaction synopsis and the turn-scoped read skips exactly the
    ///     blobs the synopsis covers: reading first would build the turn from a synopsis the database no longer has,
    ///     over history whose covered messages were never decrypted. With no map, the persisted selection wins.
    /// </remarks>
    private async Task<ChatTurnLoad> LoadTurnAsync(NodeChatStreamRequest request, CancellationToken cancellationToken)
    {
        // Reject sends to a remote-origin (view-only) conversation before any persistence happens. The guard is
        // authoritative; throwing here propagates to the hub caller.
        await _mutationGuard.EnsureMutableAsync(request.ConversationId, cancellationToken);

        var persistedSelectedPath = request.SelectedPath is not null
            ? await _persistence.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest { ConversationId = request.ConversationId, SelectedPath = request.SelectedPath, UpdatedAtUtc = NowUnixMilliseconds() }, cancellationToken)
            : null;

        // Turn-scoped read: the same message structure minus the content and metadata blobs this conversation's
        // compaction synopsis replaced. Never use it for a conversation that will be rendered or re-persisted.
        var conversation = await _persistence.GetConversationForTurnAsync(request.ConversationId, cancellationToken)
                           ?? throw new NodeChatConversationNotFoundException(request.ConversationId);

        return new ChatTurnLoad { Conversation = conversation, SelectedPath = persistedSelectedPath ?? conversation.SelectedPath };
    }

    // Mints the assistant row this turn streams into, stamped with the model that will actually run — never the raw
    // request model — and with the package's own effort precedence, so both match the run and survive reload.
    private Task<NodeChatPersistedMessageDto> PersistAssistantPlaceholderAsync(NodeChatStreamRequest request,
        ChatTurnResolution resolution,
        Guid assistantMessageId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        return _persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest
        {
            ConversationId = request.ConversationId,
            MessageId = assistantMessageId,
            RequestId = requestId,
            CreatedAtUtc = NowUnixMilliseconds(),
            Model = resolution.EffectiveModel,
            AgentDefinitionId = resolution.Resolved?.AgentDefinitionId,
            AgentName = resolution.Resolved?.AgentName,
            ReasoningEffort = EffectiveReasoningEffort(request, resolution.Resolved?.ReasoningEffort)
        },
            cancellationToken);
    }

    /// <summary>
    ///     The effort this turn runs at: a bound agent's pinned effort wins over the one the send carried.
    /// </summary>
    /// <remarks>
    ///     The pin is configuration and the send's value is the composer's selection — unless the caller says its own
    ///     is a pin too, which is what a development-workflow node's authored effort is.
    /// </remarks>
    private static string? EffectiveReasoningEffort(NodeChatStreamRequest request, string? resolvedEffort) =>
        request.ReasoningEffortOverridesAgentPin
            ? request.ReasoningEffort ?? resolvedEffort
            : resolvedEffort ?? request.ReasoningEffort;

    /// <summary>
    ///     Resolves whether this turn offers tools and, if so, which ones travel in the runtime package.
    /// </summary>
    /// <remarks>
    ///     Tools are offered only when the client asked, the node tool engine is enabled and the model advertises the
    ///     capability. A bound definition narrows the offer to its allowed set (node approval policy already applied,
    ///     custom tools merged, in <see cref="ChatTurnResolver" />); the unbound fallback builds the raw offer here and
    ///     applies the SAME tighten-only policy, or an unbound turn would bypass a node-wide one. A custom tool on that
    ///     agentless path is not session-approvable and re-prompts each time, the safe direction.
    /// </remarks>
    private async Task<ChatToolOffer> ResolveToolOfferAsync(NodeChatStreamRequest request, ChatTurnResolution resolution, CancellationToken cancellationToken)
    {
        var enableTools = await _runtimeSettings.GetEnableToolsAsync(cancellationToken);
        var offerTools = request.UseLocalTools && enableTools && resolution.SupportsTools;
        if (!offerTools)
        {
            return new ChatToolOffer { OfferTools = false, AllowedTools = null };
        }

        if (resolution.Resolved?.AllowedTools is { } resolvedAllowedTools)
        {
            return new ChatToolOffer { OfferTools = true, AllowedTools = resolvedAllowedTools };
        }

        var fallbackOffer = await _localToolOfferProvider.GetOfferedToolsAsync(resolution.ActiveModel, resolution.EffectiveModelIsCloud, cancellationToken);
        return new ChatToolOffer
        {
            OfferTools = true,
            AllowedTools = [
            .. fallbackOffer.Select(tool => tool with
            {
                RequiresApproval = _toolApprovalPolicy.RequiresApproval(tool.Name, tool.Category, tool.RequiresApproval)
            })
        ]
        };
    }

    /// <summary>
    ///     Whether this turn's attachments may travel, the load-bearing cloud-egress gate.
    /// </summary>
    /// <remarks>
    ///     Node-local attachments are private data and reach a cloud model only under
    ///     <c>KnowledgeBase:AllowCloudModelAccess</c>. An orchestration broadcasts ONE shared seed to every
    ///     participant, so a single cloud PARTICIPANT under a local root withholds the shared attachment context too —
    ///     per-participant tool stripping cannot redact a seed. The offer provider additionally withholds the file and
    ///     knowledge tools for a cloud model.
    /// </remarks>
    private bool AreAttachmentsAllowed(ChatTurnResolution resolution)
    {
        var anyCloudParticipant = resolution.Orchestration?.AnyParticipantIsCloud ?? false;
        var turnReachesCloud = resolution.EffectiveModelIsCloud || anyCloudParticipant;
        return !turnReachesCloud || _knowledgeOptions.Value.AllowCloudModelAccess;
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
        // An Orchestrator agent whose orchestration did not compile runs as a lone single agent, so emit ONE notice
        // naming the typed reason. NotOrchestrated (a Single-kind or unbound agent) has none, so the common path is silent.
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

        if (attachmentsAllowed)
        {
            return;
        }

        // Name the model the notice is about: the orchestrator's own cloud model when that is what reaches the
        // cloud, otherwise the cloud participant whose presence forced the withhold on an otherwise-local root.
        var cloudModelForNotice = resolution.EffectiveModelIsCloud
            ? resolution.EffectiveModel
            : resolution.Orchestration?.FirstCloudParticipantModel ?? resolution.EffectiveModel;
        await ReportAttachmentsWithheldIfPresentAsync(request, cloudModelForNotice, requestId, cancellationToken);

        // KB grounding rides the SAME cloud-egress gate as attachments. Plain chat only — agent mode reaches the data
        // through the gated search_knowledge_base tool, withheld by the offer provider, so the notice is not duplicated.
        if (request.UseKnowledgeBase && !offerTools)
        {
            await ReportKnowledgeWithheldAsync(cloudModelForNotice, requestId, cancellationToken);
        }
    }

    // Re-stages the AgentHome sandbox with THIS conversation's attachments. Returns the lease the caller must dispose
    // alongside any failure reason — a busy workspace yields BOTH — while a genuine cancel propagates untouched.
    private async Task<SandboxStagingOutcome> StageConversationAttachmentsAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        ConversationSandboxPreparation? preparation = null;
        string? error = null;
        try
        {
            preparation = await _conversationSandboxStager.PrepareConversationAttachmentsAsync(conversationId, cancellationToken);
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
            _logger.LogWarning(exception, "AgentHome attachment staging failed for conversation {ConversationId}.", conversationId);
            error = "The AgentHome workspace could not be prepared for this response.";
        }

        return new SandboxStagingOutcome { Preparation = preparation, Error = error };
    }

    // Terminalizes the assistant row Failed for a pre-run refusal, hence the zero-duration content-free envelope.
    // CancellationToken.None is deliberate: the row must reach a terminal even when the caller's token already fired.
    private Task<NodeChatPersistedMessageDto> TerminalizeAssistantFailureAsync(NodeChatMessageCorrelation correlation, string error)
    {
        return _persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
        {
            Correlation = correlation,
            Status = NodeChatMessageStatusValues.Failed,
            UpdatedAtUtc = NowUnixMilliseconds(),
            Error = error,
            Envelope = new AgentRunEnvelopeMetadata { InvocationId = null, DurationMs = 0L }
        },
            CancellationToken.None);
    }

    // Composes the synthetic context messages prepended to this turn. The attachment and image reads follow the CLIENT
    // token, so a disconnect stops them; knowledge retrieval follows the RUN token so a user stop aborts the search.
    private async Task<ChatTurnContext> BuildTurnContextAsync(NodeChatStreamRequest request,
        ChatTurnResolution resolution,
        bool offerTools,
        bool attachmentsAllowed,
        IReadOnlyList<string> stagedAttachmentPaths,
        string knowledgeQuery,
        CancellationTokenSource runCancellation,
        CancellationToken cancellationToken)
    {
        // The prepended context differs by mode: plain chat inlines the extracted text, agent mode injects only a
        // pointer naming the staged files, which is what stops a weak model from guessing a wrong file name.
        ConversationMessageDto? attachmentContext;
        // Knowledge grounding: a second synthetic message inlined into plain chat, plus the provenance of the inlined
        // hits. Null on every path that does not ground (agent mode, opt-out, cloud-withheld, empty retrieval).
        ConversationMessageDto? knowledgeContext = null;
        IReadOnlyList<NodeChatMessageSource>? knowledgeSources = null;
        if (offerTools)
        {
            attachmentContext = _turnContextBuilder.BuildAgentAttachmentHint(request.ConversationId, stagedAttachmentPaths);
        }
        else if (attachmentsAllowed)
        {
            attachmentContext = await _turnContextBuilder.BuildAttachmentContextAsync(request.ConversationId, request.AttachmentFileIds, cancellationToken);

            // Plain-chat knowledge grounding runs only for a node-local effective model (attachmentsAllowed already
            // encodes the locality gate). Retrieval failure degrades to no context — the turn still proceeds.
            if (request.UseKnowledgeBase)
            {
                var knowledge = await _turnContextBuilder.BuildKnowledgeContextAsync(knowledgeQuery, isRegeneratedTurn: false, runCancellation.Token);
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

        // Image parts attach INDEPENDENTLY of the tool/text branch so a vision model receives them in plain, tool-enabled
        // and agent chat alike; images have no Markdown to stage and would otherwise be dropped. Same egress gate.
        ConversationMessageDto? imageContext = null;
        if (attachmentsAllowed && resolution.SupportsVision)
        {
            imageContext = await _turnContextBuilder.BuildImageContextAsync(request.ConversationId, request.AttachmentFileIds, cancellationToken);
        }

        return new ChatTurnContext { Attachment = attachmentContext, Image = imageContext, Knowledge = knowledgeContext, KnowledgeSources = knowledgeSources };
    }

    // Assembles the runtime package the invocation runs from; the active model, effective agent and orchestration spec
    // were settled by ResolveTurnAsync and are reused unchanged. Only the invocation timeout is operator-controlled.
    private async Task<RuntimePackage> BuildRuntimePackageAsync(NodeChatStreamRequest request,
        ChatTurnResolution resolution,
        IReadOnlyList<ConversationMessageDto> conversationContext,
        IReadOnlyList<AllowedToolDto>? allowedTools,
        int invocationTimeoutSeconds,
        Guid requestId)
    {
        var resolved = resolution.Resolved;
        return _runtimePackageBuilder.Build(new LocalChatRuntimePackageRequest
        {
            InvocationId = requestId,
            ConversationId = request.ConversationId,
            ResolvedSystemPrompt = resolved?.ResolvedSystemPrompt ?? await LoadResolvedSystemPromptAsync(_localChatOptions.Value),
            ConversationContext = conversationContext,
            ModelProfile = resolution.EffectiveModel,
            AgentDefinitionVersion = resolved?.AgentDefinitionVersion ?? AgentDefinitionVersion,
            ClientNodeId = LocalChatLoopbackDefaults.ClientNodeId,
            AllowedTools = allowedTools,
            Timeouts = new TimeoutSettings
            {
                InvocationTimeoutSeconds = invocationTimeoutSeconds
            },
            ReasoningEffort = EffectiveReasoningEffort(request, resolved?.ReasoningEffort),
            OrchestrationSpec = request.SuppressAskUser ? AskUserToolOffer.Withdraw(resolution.Orchestration?.Spec) : resolution.Orchestration?.Spec,
            SupportsThinking = resolution.SupportsThinking,
            SamplingOptions = request.SamplingOptions,
            Skills = resolved?.Skills,
            CustomTools = resolved?.CustomTools,
            ReasoningBudgetEnforceable = resolution.ReasoningBudgetEnforceable,
            // Per-agent opt-out from the send-time tool-relevance filter; not hashed, so an opted-out agent keeps a
            // byte-identical config hash.
            DisableToolRelevanceFilter = resolved?.DisableToolRelevanceFilter ?? false,
            // Model-selection provenance for the runner's reasoning-effort dispatcher; false means pinned, never swap.
            // A work-session step never swaps: the graph was authored against a model, so no silent substitution.
            AllowAutoModelSwap = resolution.AllowAutoModelSwap && !request.IsWorkSessionTurn
        });
    }

    /// <summary>
    ///     Builds the post-run adaptive-memory hook, fired once when the pump persists a Completed or Failed terminal.
    /// </summary>
    /// <remarks>
    ///     It runs only when the resolved agent has the playbook enabled AND opts into extraction: retrieval rides
    ///     <c>PlaybookEnabled</c> alone, while <c>MemoryExtractionEnabled</c> gates mining NEW candidates, so a
    ///     retrieval-only agent learns nothing new. Built here rather than in the pump so it closes over the run
    ///     context and the pump stays content-free; the dispatch is fire-and-forget so it never delays the SSE.
    /// </remarks>
    private Action<InvocationState, NodeChatPumpTerminalResult>? BuildMemoryExtractionHook(ChatTurnResolution resolution,
        NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto userMessage,
        IReadOnlyDictionary<Guid, Guid>? selectedPath,
        RuntimePackage package)
    {
        return resolution.Resolved is { PlaybookEnabled: true, MemoryExtractionEnabled: true } memoryAgent
            ? ChatMemoryExtractionHook.Build(_memoryExtractionDispatcher,
                memoryAgent,
                conversation.ConversationId,
                conversation.MemoryExcluded,
                package,
                resolution.EffectiveModel,
                () => CollectUserTurns(conversation, userMessage, selectedPath))
            : null;
    }

    // Releases the AgentHome workspace as soon as the invocation is over, whatever its outcome, then lets the run's
    // result reach DrainRunAsync unchanged. Disposal is idempotent, so the scope-level disposal remains the fallback.
    private static async Task ReleaseSandboxAfterAsync(Task runTask, ConversationSandboxPreparation? preparation)
    {
        try
        {
            await runTask;
        }
        finally
        {
            if (preparation is not null)
            {
                await preparation.DisposeAsync();
            }
        }
    }

    // Drains the run after the SSE consumer is gone. The pump is observed FIRST so a persistence fault cancels the run,
    // and the subscription is disposed only once BOTH are observed, or a late terminal would read as interrupted.
    private async Task DrainRunAsync(Task pumpTask, Task runTask, CancellationTokenSource runCancellation, IDisposable eventSubscription, Guid requestId)
    {
        try
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
                _logger.LogError(exception, "Local node chat stream pump faulted; cancelling the run. RequestId={RequestId}", requestId);
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
                _logger.LogDebug(exception, "Local node chat stream run completed with an exception after teardown. RequestId={RequestId}", requestId);
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

    // The same resource AgentInstructionProvider.GetBaseScaffold reads, kept as a local literal to avoid a DI
    // dependency on IAgentInstructionProvider in this already-large constructor.
    private const string BaseScaffoldResourceName = "XE_Local_AI_Engine.AI.Agent.Instructions.BaseScaffold.txt";

    /// <summary>
    ///     Reads the embedded chat prompt for the null-definition fallback and prepends the same versioned base
    ///     scaffold a resolved agent gets, so an unbound send is covered identically to a bound one.
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
    ///     Derives the offer-time active model and the effective agent head, then defers to the shared
    ///     <see cref="ChatTurnResolver" /> for capability, definition and orchestration resolution.
    /// </summary>
    /// <remarks>
    ///     The effective-agent precedence is <c>request.AgentDefinitionId ?? conversation.AgentDefinitionId ??
    ///     (memoized) Default Assistant id</c>. Resolving the Default Assistant on a cold conversation must NOT throw:
    ///     a missing seed yields a null id, the resolver returns null, and the caller keeps the embedded default
    ///     persona, the full offer and the client "Default Assistant" label.
    /// </remarks>
    private async Task<ChatTurnResolution> ResolveTurnAsync(NodeChatStreamRequest request,
        NodeChatConversationDto conversation,
        string? activeModelOverride,
        string trimmedContent,
        CancellationToken cancellationToken)
    {
        // Precedence: explicit request model, then the operator's node default, then the static config fallback. Without
        // the middle step a tool-capable node default never becomes active, so the gate withholds run_in_agent_home.
        string? activeModel;
        var requiresInstalledChatModel = false;
        // A concrete dropdown pick (no upstream override and a non-blank request.Model) must win over a bound agent's
        // pinned ModelProfile for BOTH the run and the attribution, so it suppresses the pin in the resolve below.
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
            // "Local runtime default" resolves to an installed GGUF chat model, never Ollama, and honors the node
            // default only when it is one. A null resolve flags the turn so RunInvocationAsync says ModelNotInstalled.
            var nodeSettings = await _nodeSettingsStore.LoadAsync(cancellationToken);
            activeModel = await _localDefaultChatModelResolver.ResolveAsync(nodeSettings.DefaultModelName, cancellationToken);
            requiresInstalledChatModel = activeModel is null;
        }

        // Effective-agent precedence: the per-send selection, then the conversation binding, then the seeded Default
        // Assistant. The default id is memoized for the process lifetime so the mode-off hot path skips a DB read.
        var effectiveAgentId = request.AgentDefinitionId
                               ?? conversation.AgentDefinitionId
                               ?? await _defaultAgentProvider.GetDefaultAgentIdAsync(cancellationToken);

        // The just-sent user turn is the relevance-retrieval query, inert below the threshold or unbound. The shared
        // resolver gates thinking and tools by capability and returns the effective model both stamps come from.
        return await _turnResolver.ResolveAsync(activeModel, requiresInstalledChatModel, effectiveAgentId, trimmedContent, userPickedConcreteModel, cancellationToken);
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

    // The conversation this send turn is built from, plus the variant selection that shapes its history.
    private sealed record ChatTurnLoad
    {
        public required NodeChatConversationDto Conversation { get; init; }

        public required IReadOnlyDictionary<Guid, Guid>? SelectedPath { get; init; }
    }

    // The tool offer for one turn: whether tools are offered at all, and the allow-list that travels in the runtime
    // package (null whenever nothing is offered).
    private sealed record ChatToolOffer
    {
        public required bool OfferTools { get; init; }

        public required IReadOnlyList<AllowedToolDto>? AllowedTools { get; init; }
    }

    // The outcome of staging a conversation's attachments into the AgentHome sandbox. A busy workspace yields BOTH a
    // lease to dispose and a refusal reason, so neither field implies the other is null.
    private sealed record SandboxStagingOutcome
    {
        public required ConversationSandboxPreparation? Preparation { get; init; }

        public required string? Error { get; init; }
    }

    // The synthetic context messages prepended to one turn, plus the provenance of any inlined knowledge hits.
    private sealed record ChatTurnContext
    {
        public required ConversationMessageDto? Attachment { get; init; }

        public required ConversationMessageDto? Image { get; init; }

        public required ConversationMessageDto? Knowledge { get; init; }

        public required IReadOnlyList<NodeChatMessageSource>? KnowledgeSources { get; init; }
    }
}
