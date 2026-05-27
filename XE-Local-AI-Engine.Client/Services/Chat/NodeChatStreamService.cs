namespace XE_Local_AI_Engine.Client.Services.Chat;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;

public sealed class NodeChatStreamService(
    INodeChatPersistenceService persistence,
    INodeChatInvocationPump invocationPump,
    INodeChatMutationGuard mutationGuard,
    ILocalChatRuntimePackageBuilder runtimePackageBuilder,
    IInvocationRunner invocationRunner,
    IWorkerEventDispatcher eventDispatcher,
    IOptions<LocalChatAgentOptions> localChatOptions,
    INodeChatStreamCancellationRegistry cancellationRegistry,
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

        var assistantPlaceholder = await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(request.ConversationId,
                assistantMessageId,
                requestId,
                NowUnixMilliseconds(),
                request.Model),
            cancellationToken).ConfigureAwait(false);
        yield return ToMessageEvent(ChatStreamEventTypes.AssistantPending, correlation, assistantPlaceholder, sequence.Next());

        // The turn is Queued until the collision-queue lease is acquired in RunInvocationAsync; it transitions to
        // Streaming only when the invocation actually starts. This keeps a turn waiting behind another invocation
        // visibly "queued" rather than prematurely "streaming".
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

        void OnToolCallLifecycleChanged(object? _, ToolCallLifecycleChangedEventArgs args)
        {
            if (args.Payload.InvocationId == requestId)
            {
                eventChannel.Writer.TryWrite(ToToolCallEvent(correlation, args.Payload, sequence.Next()));
            }
        }

        eventDispatcher.InvocationStateChanged += OnInvocationStateChanged;
        eventDispatcher.ToolCallLifecycleChanged += OnToolCallLifecycleChanged;

        // Tools are offered to the loopback agent only when the client asked for them AND the node has the agent
        // tool engine enabled. There is no local tool catalog yet (all beta tools are non-approval, none defined),
        // so the offered set is empty today; the flag wiring keeps the gate in place for when one lands.
        var offerTools = request.UseLocalTools && localChatOptions.Value.EnableTools;
        IReadOnlyList<AllowedToolDto>? allowedTools = offerTools ? [] : null;

        var package = runtimePackageBuilder.Build(new LocalChatRuntimePackageRequest(requestId,
            request.ConversationId,
            LoadResolvedSystemPrompt(localChatOptions.Value),
            BuildConversationContext(conversation, userMessage),
            request.Model ?? localChatOptions.Value.DefaultModel,
            AgentDefinitionVersion,
            LocalChatLoopbackDefaults.ClientNodeId,
            AllowedTools: allowedTools,
            RequestedCapabilities: [LocalChatLoopbackDefaults.RequestedCapability]));

        var pumpTask = PumpInvocationStatesAsync(stateChannel.Reader,
            eventChannel.Writer,
            correlation,
            request.Model,
            sequence,
            linkedCancellation.Token);
        var runTask = RunInvocationAsync(package,
            assistantMessageId,
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

    private static IReadOnlyList<ConversationMessageDto> BuildConversationContext(NodeChatConversationDto conversation,
        NodeChatPersistedMessageDto userMessage)
    {
        var messages = conversation.Messages
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
