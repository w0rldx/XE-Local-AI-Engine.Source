namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeChatStreamServiceTests
{
    [Test]
    public async Task SendMessageAsync_WhenInvocationReportsUsage_StreamsTerminalTokenCounts()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var terminalRequest = default(NodeChatTerminalizeMessageRequest);
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, request => terminalRequest = request);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new CompletingInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);
        var events = new List<ChatStreamEvent>();

        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)).ConfigureAwait(false))
        {
            events.Add(streamEvent);
        }

        var completed = events.Single(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantCompleted);
        AssertEx.Equal(10, completed.InputTokens);
        AssertEx.Equal(3, completed.OutputTokens);
        AssertEx.Equal(13, completed.TotalTokens);
        AssertEx.Equal(1, completed.ReasoningTokens);
        AssertEx.Equal(10, terminalRequest!.InputCount);
        AssertEx.Equal(13, terminalRequest.TotalCount);
    }

    [Test]
    public async Task SendMessageAsync_WhenConsumerCancelsEnumeration_TerminalizesAssistantAsCancelled()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var terminalRequest = default(NodeChatTerminalizeMessageRequest);
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, request => terminalRequest = request);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new StreamingUntilCancelledInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);
        using var cancellation = new CancellationTokenSource();

        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                               "hello",
                               MessageId: assistantMessageId,
                               RequestId: requestId),
                           cancellation.Token).ConfigureAwait(false))
        {
            if (streamEvent.Type == ChatStreamEventTypes.AssistantDelta)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                break;
            }
        }

        await AssertEx.EventuallyAsync(() => terminalRequest is not null, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        AssertEx.Equal(NodeChatMessageStatusValues.Cancelled, terminalRequest!.Status);
        AssertEx.Equal(conversationId, terminalRequest.Correlation.ConversationId);
        AssertEx.Equal(assistantMessageId, terminalRequest.Correlation.MessageId);
        AssertEx.Equal(requestId, terminalRequest.Correlation.RequestId);
        AssertEx.Equal("thinking", terminalRequest.Reasoning);
    }

    private static INodeChatPersistenceService CreatePersistence(Guid conversationId,
        Guid assistantMessageId,
        Guid requestId,
        Action<NodeChatTerminalizeMessageRequest> terminalized)
    {
        var persistence = Substitute.For<INodeChatPersistenceService>();
        var conversation = new NodeChatConversationDto(conversationId,
            "test",
            null,
            1,
            1,
            false,
            []);
        var userMessage = new NodeChatPersistedMessageDto(Guid.NewGuid(),
            conversationId,
            null,
            1,
            "user",
            "hello",
            null,
            NodeChatMessageStatusValues.Completed,
            1,
            1,
            null,
            null,
            null);
        var assistantPending = CreateAssistantMessage(conversationId,
            assistantMessageId,
            requestId,
            NodeChatMessageStatusValues.Pending,
            string.Empty,
            null);
        var assistantStreaming = assistantPending with
        {
            Status = NodeChatMessageStatusValues.Streaming
        };

        persistence.GetConversationAsync(conversationId, Arg.Any<CancellationToken>())
                   .Returns(conversation);
        persistence.PersistUserMessageAsync(Arg.Any<NodeChatPersistUserMessageRequest>(), Arg.Any<CancellationToken>())
                   .Returns(userMessage);
        persistence.CreateAssistantPlaceholderAsync(Arg.Any<NodeChatCreateAssistantPlaceholderRequest>(), Arg.Any<CancellationToken>())
                   .Returns(assistantPending);
        persistence.MarkAssistantStreamingAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                   .Returns(assistantStreaming);
        persistence.FlushAssistantPartialAsync(Arg.Any<NodeChatPartialFlushRequest>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo =>
                   {
                       var request = callInfo.ArgAt<NodeChatPartialFlushRequest>(0);
                       return CreateAssistantMessage(conversationId,
                           assistantMessageId,
                           requestId,
                           NodeChatMessageStatusValues.Streaming,
                           request.Content,
                           request.Reasoning);
                   });
        persistence.TerminalizeAssistantMessageAsync(Arg.Do<NodeChatTerminalizeMessageRequest>(terminalized), Arg.Any<CancellationToken>())
                   .Returns(callInfo =>
                   {
                       var request = callInfo.ArgAt<NodeChatTerminalizeMessageRequest>(0);
                       return CreateAssistantMessage(conversationId,
                           assistantMessageId,
                           requestId,
                           request.Status,
                           request.Content ?? string.Empty,
                           request.Reasoning,
                           request.Error);
                   });

        return persistence;
    }

    private static NodeChatPersistedMessageDto CreateAssistantMessage(Guid conversationId,
        Guid assistantMessageId,
        Guid requestId,
        string status,
        string content,
        string? reasoning,
        string? error = null)
    {
        return new NodeChatPersistedMessageDto(assistantMessageId,
            conversationId,
            requestId,
            2,
            "assistant",
            content,
            reasoning,
            status,
            1,
            1,
            null,
            error,
            null);
    }

    private sealed class StreamingUntilCancelledInvocationRunner(RecordingWorkerEventDispatcher dispatcher) : IInvocationRunner
    {
        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            await dispatcher.ReportInvocationThinkingChunkAsync(context.Package.InvocationId, "thinking").ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(string.Empty);
        }

        public void Cancel(Guid invocationId)
        {
        }

        public void CancelAll()
        {
        }

        public void CleanupStaleToolCalls(TimeSpan maxAge)
        {
        }

        public void ResolveApprovalResult(ApprovalResolvedEvent evt)
        {
        }

        public void ResolveToolCallResult(ToolCallResultEvent evt)
        {
        }
    }

    private sealed class CompletingInvocationRunner(RecordingWorkerEventDispatcher dispatcher) : IInvocationRunner
    {
        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            await dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, "answer").ConfigureAwait(false);
            await dispatcher.ReportInvocationThinkingChunkAsync(context.Package.InvocationId, "thinking").ConfigureAwait(false);
            await dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId, 10, 3, 13, 1).ConfigureAwait(false);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(string.Empty);
        }

        public void Cancel(Guid invocationId)
        {
        }

        public void CancelAll()
        {
        }

        public void CleanupStaleToolCalls(TimeSpan maxAge)
        {
        }

        public void ResolveApprovalResult(ApprovalResolvedEvent evt)
        {
        }

        public void ResolveToolCallResult(ToolCallResultEvent evt)
        {
        }
    }

    private sealed class RecordingWorkerEventDispatcher : IWorkerEventDispatcher
    {
        public event EventHandler<InvocationStateChangedEventArgs>? InvocationStateChanged;

        public InvocationState? CurrentInvocation { get; private set; }

        public bool IsAcceptingRemoteInvocations => true;

        public void StopAcceptingRemoteInvocations()
        {
        }

        public Task DispatchInvocationAssignedAsync(EncryptedRuntimePackageDto package)
        {
            return Task.CompletedTask;
        }

        public Task DispatchInvocationAssignedV2Async(InvocationAssignedEnvelope envelope)
        {
            return Task.CompletedTask;
        }

        public Task DispatchToolCallResultAsync(ToolCallResultEvent evt)
        {
            return Task.CompletedTask;
        }

        public Task DispatchDisconnectRequestedAsync(DisconnectRequestedEvent evt)
        {
            return Task.CompletedTask;
        }

        public Task DispatchApprovalResolvedAsync(ApprovalResolvedEvent evt)
        {
            return Task.CompletedTask;
        }

        public Task DispatchInvocationCancelledAsync(InvocationCancelledEvent evt)
        {
            return Task.CompletedTask;
        }

        public Task ReportInvocationAssignedAsync(RuntimePackage package)
        {
            CurrentInvocation = new InvocationState
            {
                InvocationId = package.InvocationId,
                ConversationId = package.ConversationId,
                Status = InvocationStatus.Assigned,
                StartedAt = DateTimeOffset.UtcNow,
                LastUpdatedAt = DateTimeOffset.UtcNow
            };
            RaiseChanged();
            return Task.CompletedTask;
        }

        public Task ReportInvocationStreamChunkAsync(Guid invocationId, string chunk)
        {
            if (CurrentInvocation is null)
            {
                return Task.CompletedTask;
            }

            CurrentInvocation.Status = InvocationStatus.Running;
            CurrentInvocation.StreamedContent += chunk;
            CurrentInvocation.StreamedChunkCount++;
            CurrentInvocation.LastUpdatedAt = DateTimeOffset.UtcNow;
            RaiseChanged();
            return Task.CompletedTask;
        }

        public Task ReportInvocationThinkingChunkAsync(Guid invocationId, string chunk)
        {
            if (CurrentInvocation is null)
            {
                return Task.CompletedTask;
            }

            CurrentInvocation.Status = InvocationStatus.Running;
            CurrentInvocation.StreamedThinkingContent += chunk;
            CurrentInvocation.StreamedThinkingChunkCount++;
            CurrentInvocation.LastUpdatedAt = DateTimeOffset.UtcNow;
            RaiseChanged();
            return Task.CompletedTask;
        }

        public Task ReportInvocationCompletedAsync(Guid invocationId, int? inputTokens = null, int? outputTokens = null, int? totalTokens = null, int? reasoningTokens = null)
        {
            if (CurrentInvocation is not null)
            {
                CurrentInvocation.Status = InvocationStatus.Completed;
                CurrentInvocation.CompletedAt = DateTimeOffset.UtcNow;
                CurrentInvocation.InputTokens = inputTokens;
                CurrentInvocation.OutputTokens = outputTokens;
                CurrentInvocation.TotalTokens = totalTokens;
                CurrentInvocation.ReasoningTokens = reasoningTokens;
                RaiseChanged();
            }

            return Task.CompletedTask;
        }

        public Task ReportInvocationFailedAsync(Guid invocationId, string failureMessage, FailureCategory failureCategory)
        {
            if (CurrentInvocation is not null)
            {
                CurrentInvocation.Status = failureCategory == FailureCategory.Cancelled ? InvocationStatus.Cancelled : InvocationStatus.Failed;
                CurrentInvocation.Error = failureMessage;
                CurrentInvocation.FailureCategory = failureCategory;
                CurrentInvocation.CompletedAt = DateTimeOffset.UtcNow;
                RaiseChanged();
            }

            return Task.CompletedTask;
        }

        public Task ReportToolCallRequestedAsync(ToolCallRequestPayload payload)
        {
            return Task.CompletedTask;
        }

        public Task ReportApprovalRequestedAsync(ApprovalRequestPayload payload)
        {
            return Task.CompletedTask;
        }

        private void RaiseChanged()
        {
            InvocationStateChanged?.Invoke(this, new InvocationStateChangedEventArgs(CurrentInvocation!));
        }
    }
}
