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
    INodeChatStreamCancellationRegistry cancellationRegistry,
    ILocalToolOfferProvider localToolOfferProvider,
    IAgentDefinitionResolver agentDefinitionResolver,
    IAgentDefinitionStore agentDefinitionStore,
    IOrchestrationResolver orchestrationResolver,
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

        // Reuse the backend mint: creates the sibling placeholder (pending, shared variant_group_id, parent copied
        // from the original) — never an in-place overwrite. We do NOT duplicate mint logic here.
        var variant = await persistence.CreateMessageVariantAsync(new NodeChatCreateMessageVariantRequest(conversationId,
                              originalMessageId,
                              newMessageId,
                              requestId,
                              startedAtUtc,
                              original.Model),
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

        void OnToolCallLifecycleChanged(object? _, ToolCallLifecycleChangedEventArgs args)
        {
            if (args.Payload.InvocationId == requestId)
            {
                eventChannel.Writer.TryWrite(ToToolCallEvent(correlation, args.Payload, sequence.Next()));
            }
        }

        eventDispatcher.InvocationStateChanged += OnInvocationStateChanged;
        eventDispatcher.ToolCallLifecycleChanged += OnToolCallLifecycleChanged;

        var activeModel = original.Model ?? localChatOptions.Value.DefaultModel;

        // Mirror the send path exactly (NodeChatStreamService): resolve the conversation's bound definition so a
        // regenerated turn reruns with the SAME persona, tools, model, and version a fresh send would — a missed
        // hydration here would make reruns diverge from sends. A null result keeps the default persona.
        // Playbook P5: a regenerate answers the SAME question as the original, so the relevance-retrieval query is the
        // user turn that precedes the turn being regenerated (the same cutoff the regeneration context anchors on). When
        // that user turn cannot be found the query is null and the resolver falls back to the full static prepend.
        var retrievalQuery = ResolvePrecedingUserTurnContent(conversation, original);

        var resolved = await agentDefinitionResolver.ResolveAsync(conversation.AgentDefinitionId, activeModel, retrievalQuery, cancellationToken).ConfigureAwait(false);

        // Symmetric with the send path (NodeChatStreamService): when the bound definition is a tool-capable
        // orchestrator, resolve a compiled orchestration spec so a regenerated turn reruns through the SAME handoff
        // workflow a fresh send would — a missed hydration here would make reruns diverge from sends. A null result
        // keeps the single-agent path. The same preceding user turn drives per-participant playbook retrieval.
        var orchestration = await ResolveOrchestrationAsync(conversation.AgentDefinitionId, activeModel, retrievalQuery, cancellationToken).ConfigureAwait(false);

        // Symmetric with the send path (NodeChatStreamService): offer tools to the loopback agent only when the
        // client asked AND the node has the tool engine enabled. When offered, the catalog's local tools travel in
        // the runtime package as the offer list; the invocation factory resolves the matching executables from the
        // registry by name. A bound definition narrows that offer to its allowed set.
        var offerTools = useLocalTools && localChatOptions.Value.EnableTools;
        var allowedTools = offerTools
            ? resolved?.AllowedTools ?? localToolOfferProvider.GetOfferedTools(activeModel)
            : null;

        var package = runtimePackageBuilder.Build(new LocalChatRuntimePackageRequest(requestId,
            conversationId,
            resolved?.ResolvedSystemPrompt ?? LoadResolvedSystemPrompt(localChatOptions.Value),
            BuildRegenerationContext(conversation, original, selectedPath),
            resolved?.ModelProfile ?? activeModel,
            resolved?.AgentDefinitionVersion ?? AgentDefinitionVersion,
            LocalChatLoopbackDefaults.ClientNodeId,
            AllowedTools: allowedTools,
            RequestedCapabilities: [LocalChatLoopbackDefaults.RequestedCapability],
            ReasoningEffort: resolved?.ReasoningEffort ?? reasoningEffort,
            OrchestrationSpec: orchestration?.Spec));

        var pumpTask = PumpInvocationStatesAsync(stateChannel.Reader,
            eventChannel.Writer,
            correlation,
            original.Model,
            sequence,
            linkedCancellation.Token);
        var runTask = RunInvocationAsync(package,
            placeholder.MessageId,
            stateChannel.Writer,
            eventChannel.Writer,
            correlation,
            requestId,
            sequence,
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
                    await eventWriter.WriteAsync(ToMessageEvent(ChatStreamEventTypes.AssistantDelta,
                            correlation,
                            flush.Persisted,
                            sequence.Next(),
                            flush.ContentDelta,
                            flush.ReasoningDelta),
                        cancellationToken).ConfigureAwait(false);
                }

                if (NodeChatInvocationPump.IsTerminal(state.Status))
                {
                    var terminal = await invocationPump.TerminalizeAsync(correlation, state, requestedModel).ConfigureAwait(false);
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
    ///     Mirrors <c>NodeChatStreamService.ResolveOrchestrationAsync</c>: resolves a compiled orchestration spec for a
    ///     bound orchestrator definition (loop P5), or <c>null</c> to rerun single-agent. Only a bound conversation
    ///     triggers the extra record fetch, so the single-agent path stays byte-identical.
    /// </summary>
    private async Task<ResolvedOrchestration?> ResolveOrchestrationAsync(Guid? agentDefinitionId,
        string? activeModel,
        string? retrievalQuery,
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

        return await orchestrationResolver.ResolveAsync(definition, activeModel, retrievalQuery, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     The content of the latest USER turn strictly before the original's variant group — the question the
    ///     regenerate re-answers, used as the Playbook P5 relevance-retrieval query. Mirrors the cutoff anchor used by
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
}
