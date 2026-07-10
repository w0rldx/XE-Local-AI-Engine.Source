namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
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
                eventChannel.Writer.TryWrite(ChatStreamEventMapper.ToolCallEvent(correlation.ConversationId, correlation.MessageId, correlation.RequestId, args.Payload, NowUnixMilliseconds(), toolSequence));
            }
        }

        void OnTurnNoticeChanged(object? _, TurnNoticeChangedEventArgs args)
        {
            if (args.Payload.InvocationId == requestId)
            {
                var noticeSequence = sequence.Next();
                ChatStreamEventMapper.AccumulateNotice(parts, args.Payload, noticeSequence);
                eventChannel.Writer.TryWrite(ChatStreamEventMapper.NoticeEvent(correlation.ConversationId, correlation.MessageId, correlation.RequestId, args.Payload, NowUnixMilliseconds(), noticeSequence));
            }
        }

        eventDispatcher.InvocationStateChanged += OnInvocationStateChanged;
        eventDispatcher.ToolCallLifecycleChanged += OnToolCallLifecycleChanged;
        eventDispatcher.TurnNoticeChanged += OnTurnNoticeChanged;

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
            ? ChatMemoryExtractionHook.Build(memoryExtractionDispatcher,
                memoryAgent,
                conversation.ConversationId,
                conversation.MemoryExcluded,
                package,
                resolution.EffectiveModel,
                () => CollectUserTurns(conversation, original, selectedPath))
            : null;

        var pumpTask = invocationStatePump.PumpAsync(stateChannel.Reader,
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
            eventDispatcher.TurnNoticeChanged -= OnTurnNoticeChanged;
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
}
