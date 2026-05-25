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
        var conversation = await persistence.GetConversationAsync(request.ConversationId, cancellationToken).ConfigureAwait(false)
                           ?? throw new InvalidOperationException("The node chat conversation was not found.");
        var trimmedContent = request.Content.Trim();
        var userMessageId = request.UserMessageId.GetValueOrDefault(Guid.NewGuid());
        var assistantMessageId = request.MessageId.GetValueOrDefault(Guid.NewGuid());
        var requestId = request.RequestId.GetValueOrDefault(Guid.NewGuid());
        var correlation = new NodeChatMessageCorrelation(request.ConversationId, assistantMessageId, requestId);
        var sequence = 0L;
        var startedAtUtc = NowUnixMilliseconds();

        var userMessage = await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(request.ConversationId, userMessageId, trimmedContent, startedAtUtc),
            cancellationToken).ConfigureAwait(false);
        yield return ToMessageEvent(ChatStreamEventTypes.UserMessagePersisted, correlation, userMessage, sequence++);

        var assistantPlaceholder = await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(request.ConversationId,
                assistantMessageId,
                requestId,
                NowUnixMilliseconds(),
                request.Model),
            cancellationToken).ConfigureAwait(false);
        yield return ToMessageEvent(ChatStreamEventTypes.AssistantPending, correlation, assistantPlaceholder, sequence++);

        var streamingMessage = await persistence.MarkAssistantStreamingAsync(correlation, NowUnixMilliseconds(), cancellationToken).ConfigureAwait(false);
        yield return ToMessageEvent(ChatStreamEventTypes.AssistantStreaming, correlation, streamingMessage, sequence++);

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
            SingleWriter = true
        });

        void OnInvocationStateChanged(object? _, InvocationStateChangedEventArgs args)
        {
            if (args.State.InvocationId == requestId)
            {
                stateChannel.Writer.TryWrite(args.State);
            }
        }

        eventDispatcher.InvocationStateChanged += OnInvocationStateChanged;

        var package = runtimePackageBuilder.Build(new LocalChatRuntimePackageRequest(requestId,
            request.ConversationId,
            LoadResolvedSystemPrompt(localChatOptions.Value),
            BuildConversationContext(conversation, userMessage),
            request.Model ?? localChatOptions.Value.DefaultModel,
            AgentDefinitionVersion,
            LocalChatLoopbackDefaults.ClientNodeId,
            RequestedCapabilities: [LocalChatLoopbackDefaults.RequestedCapability]));

        var pumpTask = PumpInvocationStatesAsync(stateChannel.Reader,
            eventChannel.Writer,
            correlation,
            request.Model,
            sequence,
            linkedCancellation.Token);
        var runTask = RunInvocationAsync(package, assistantMessageId, stateChannel.Writer, requestId, linkedCancellation.Token);

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
        Guid requestId,
        CancellationToken cancellationToken)
    {
        try
        {
            await eventDispatcher.ReportInvocationAssignedAsync(package).ConfigureAwait(false);
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
            stateWriter.TryComplete();
        }
    }

    private async Task PumpInvocationStatesAsync(ChannelReader<InvocationState> stateReader,
        ChannelWriter<ChatStreamEvent> eventWriter,
        NodeChatMessageCorrelation correlation,
        string? requestedModel,
        long startingSequence,
        CancellationToken cancellationToken)
    {
        var sequence = startingSequence;
        var lastContent = string.Empty;
        var lastReasoning = string.Empty;
        var terminalPersisted = false;

        try
        {
            await foreach (var state in stateReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var hasContentDelta = state.StreamedContent.Length > lastContent.Length;
                var hasReasoningDelta = state.StreamedThinkingContent.Length > lastReasoning.Length;

                if (hasContentDelta || hasReasoningDelta)
                {
                    var contentDelta = hasContentDelta ? state.StreamedContent[lastContent.Length..] : null;
                    var reasoningDelta = hasReasoningDelta ? state.StreamedThinkingContent[lastReasoning.Length..] : null;
                    lastContent = state.StreamedContent;
                    lastReasoning = state.StreamedThinkingContent;

                    var persisted = await persistence.FlushAssistantPartialAsync(new NodeChatPartialFlushRequest(correlation,
                            lastContent,
                            string.IsNullOrEmpty(lastReasoning) ? null : lastReasoning,
                            NowUnixMilliseconds()),
                        cancellationToken).ConfigureAwait(false);

                    await eventWriter.WriteAsync(ToMessageEvent(ChatStreamEventTypes.AssistantDelta,
                            correlation,
                            persisted,
                            sequence++,
                            contentDelta,
                            reasoningDelta),
                        cancellationToken).ConfigureAwait(false);
                }

                if (state.Status is InvocationStatus.Completed or InvocationStatus.Cancelled or InvocationStatus.Failed)
                {
                    var terminalStatus = state.Status switch
                    {
                        InvocationStatus.Completed => NodeChatMessageStatusValues.Completed,
                        InvocationStatus.Cancelled => NodeChatMessageStatusValues.Cancelled,
                        _ => NodeChatMessageStatusValues.Failed
                    };
                    var eventType = state.Status switch
                    {
                        InvocationStatus.Completed => ChatStreamEventTypes.AssistantCompleted,
                        InvocationStatus.Cancelled => ChatStreamEventTypes.AssistantCancelled,
                        _ => ChatStreamEventTypes.AssistantFailed
                    };

                    var persisted = await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                            terminalStatus,
                            NowUnixMilliseconds(),
                            state.StreamedContent,
                            string.IsNullOrEmpty(state.StreamedThinkingContent) ? null : state.StreamedThinkingContent,
                            state.Error,
                            state.ModelUsed ?? requestedModel),
                        CancellationToken.None).ConfigureAwait(false);
                    terminalPersisted = true;

                    await eventWriter.WriteAsync(ToMessageEvent(eventType, correlation, persisted, sequence++), CancellationToken.None).ConfigureAwait(false);
                    break;
                }
            }

            if (!terminalPersisted)
            {
                await TerminalizeInterruptedStreamAsync(eventWriter,
                    correlation,
                    sequence,
                    lastContent,
                    lastReasoning,
                    cancellationToken.IsCancellationRequested).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !terminalPersisted)
        {
            await TerminalizeInterruptedStreamAsync(eventWriter,
                correlation,
                sequence,
                lastContent,
                lastReasoning,
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
        string lastContent,
        string lastReasoning,
        bool wasCancelled)
    {
        var status = wasCancelled
            ? NodeChatMessageStatusValues.Cancelled
            : NodeChatMessageStatusValues.Interrupted;
        var eventType = wasCancelled
            ? ChatStreamEventTypes.AssistantCancelled
            : ChatStreamEventTypes.AssistantInterrupted;

        var persisted = await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                status,
                NowUnixMilliseconds(),
                lastContent,
                string.IsNullOrEmpty(lastReasoning) ? null : lastReasoning,
                status),
            CancellationToken.None).ConfigureAwait(false);

        await eventWriter.WriteAsync(ToMessageEvent(eventType, correlation, persisted, sequence), CancellationToken.None).ConfigureAwait(false);
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
        string? reasoningDelta = null)
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
            message.Model);
    }

    private long NowUnixMilliseconds()
    {
        return timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }
}
