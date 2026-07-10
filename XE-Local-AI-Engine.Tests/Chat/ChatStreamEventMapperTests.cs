namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ChatStreamEventMapperTests
{
    private const long Timestamp = 1_700_000_000_000L;

    [Test]
    public void ToolCallEvent_RequestedPhase_CarriesArgumentsAndApprovalAndNullsCompletionFields()
    {
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var payload = NewPayload("call-1", "search", ToolCallLifecyclePhase.Requested, arguments: "{\"q\":1}", requiresApproval: true);

        var mapped = ChatStreamEventMapper.ToolCallEvent(conversationId, messageId, requestId, payload, Timestamp, sequence: 7);

        AssertEx.Equal(ChatStreamEventTypes.ToolCallRequested, mapped.Type);
        AssertEx.Equal(NodeChatMessageStatusValues.Streaming, mapped.Status);
        AssertEx.Equal(conversationId, mapped.ConversationId);
        AssertEx.Equal(messageId, mapped.MessageId);
        AssertEx.Equal(requestId, mapped.RequestId);
        AssertEx.Equal(expected: 7L, mapped.Sequence);
        AssertEx.Equal(Timestamp, mapped.OccurredAtUtc);
        AssertEx.Equal("call-1", mapped.ToolCallId);
        AssertEx.Equal("search", mapped.ToolName);
        AssertEx.Equal("{\"q\":1}", mapped.Arguments);
        AssertEx.Equal(expected: true, mapped.RequiresApproval);
        // The requested phase carries no result/error yet.
        AssertEx.Null(mapped.Result);
        AssertEx.Null(mapped.IsError);
    }

    [Test]
    public void ToolCallEvent_CompletedPhase_CarriesResultAndErrorAndNullsRequestFields()
    {
        var payload = NewPayload("call-2", "search", ToolCallLifecyclePhase.Completed, result: "42", isError: true);

        var mapped = ChatStreamEventMapper.ToolCallEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), payload, Timestamp, sequence: 3);

        AssertEx.Equal(ChatStreamEventTypes.ToolCallCompleted, mapped.Type);
        AssertEx.Equal("42", mapped.Result);
        AssertEx.Equal(expected: true, mapped.IsError);
        // The completed phase re-emits neither arguments nor the approval flag.
        AssertEx.Null(mapped.Arguments);
        AssertEx.Null(mapped.RequiresApproval);
    }

    [Test]
    public void ToolCallEvent_LiveAndResumeIdentities_ProduceIdenticalWireFieldsExceptIds()
    {
        // The live send/regenerate paths stamp a (conversation, message, request) correlation; the resume path stamps
        // the invocation id as BOTH the message id and the request id. Every OTHER field must be byte-identical so a
        // resumed stream renders the same tool cards the original stream did — this is the anti-drift guarantee.
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();

        foreach (var payload in new[]
                 {
                     NewPayload("c", "t", ToolCallLifecyclePhase.Requested, arguments: "{}", requiresApproval: true),
                     NewPayload("c", "t", ToolCallLifecyclePhase.Completed, result: "r", isError: false)
                 })
        {
            var live = ChatStreamEventMapper.ToolCallEvent(conversationId, messageId, requestId, payload, Timestamp, sequence: 5);
            var resume = ChatStreamEventMapper.ToolCallEvent(conversationId, invocationId, invocationId, payload, Timestamp, sequence: 5);

            // Rebasing the live event's identity fields onto the resume identity must yield the resume event byte-for-byte
            // (ChatStreamEvent is a record, so this is a full value comparison of every wire field at once). If any
            // non-identity field diverged, the records would not be equal.
            AssertEx.Equal(live with { MessageId = invocationId, RequestId = invocationId }, resume);
            AssertEx.Equal(invocationId, resume.MessageId);
            AssertEx.Equal(invocationId, resume.RequestId);
        }
    }

    [Test]
    public void MessageEvent_MapsPersistedFieldsAndPrefersTokenOverrides()
    {
        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var message = NewMessage(correlation, content: "answer", reasoning: "because", status: NodeChatMessageStatusValues.Completed, model: "model-x", inputCount: 11, outputCount: 22);

        // No token overrides -> the persisted counts flow through.
        var passthrough = ChatStreamEventMapper.MessageEvent(ChatStreamEventTypes.AssistantCompleted, correlation, message, Timestamp, sequence: 1);
        AssertEx.Equal(ChatStreamEventTypes.AssistantCompleted, passthrough.Type);
        AssertEx.Equal(correlation.ConversationId, passthrough.ConversationId);
        AssertEx.Equal(correlation.MessageId, passthrough.MessageId);
        AssertEx.Equal(correlation.RequestId, passthrough.RequestId);
        AssertEx.Equal("answer", passthrough.Content);
        AssertEx.Equal("because", passthrough.Reasoning);
        AssertEx.Equal("model-x", passthrough.Model);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, passthrough.Status);
        AssertEx.Equal(expected: 11, passthrough.InputTokens);
        AssertEx.Equal(expected: 22, passthrough.OutputTokens);

        // An explicit override wins over the persisted count (the terminal path stamps live token totals).
        var overridden = ChatStreamEventMapper.MessageEvent(ChatStreamEventTypes.AssistantCompleted, correlation, message, Timestamp, sequence: 2, inputTokens: 99);
        AssertEx.Equal(expected: 99, overridden.InputTokens);
        AssertEx.Equal(expected: 22, overridden.OutputTokens);
    }

    [Test]
    public void NoticeEvent_MapsKindAndMessageOntoTheAssistantNoticeType()
    {
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var payload = new TurnNoticePayload
        {
            InvocationId = Guid.NewGuid(),
            Kind = TurnNoticeKind.ModelSubstituted,
            Message = "Model 'x' could not be verified; fell back to 'y'.",
            Detail = "y"
        };

        var mapped = ChatStreamEventMapper.NoticeEvent(conversationId, messageId, requestId, payload, Timestamp, sequence: 4);

        AssertEx.Equal(ChatStreamEventTypes.AssistantNotice, mapped.Type);
        AssertEx.Equal(conversationId, mapped.ConversationId);
        AssertEx.Equal(messageId, mapped.MessageId);
        AssertEx.Equal(requestId, mapped.RequestId);
        AssertEx.Equal(NodeChatMessageStatusValues.Streaming, mapped.Status);
        AssertEx.Equal(expected: 4L, mapped.Sequence);
        AssertEx.Equal(nameof(TurnNoticeKind.ModelSubstituted), mapped.NoticeKind);
        AssertEx.Equal(payload.Message, mapped.NoticeMessage);
        // A notice is not a tool-call event: none of the tool-specific fields are populated.
        AssertEx.Null(mapped.ToolCallId);
        AssertEx.Null(mapped.Result);
    }

    [Test]
    public void AccumulateNotice_AppendsANoticePartCarryingKindAndMessage()
    {
        var parts = new NodeChatPartAccumulator();
        var payload = new TurnNoticePayload
        {
            InvocationId = Guid.NewGuid(),
            Kind = TurnNoticeKind.HistoryTruncated,
            Message = "Conversation history was trimmed to fit the model's context window (2 older message(s) dropped, 0 tool result(s) shortened)."
        };

        ChatStreamEventMapper.AccumulateNotice(parts, payload, sequence: 9);

        var snapshot = parts.Snapshot();
        AssertEx.Equal(expected: 1, snapshot.Count);
        var part = snapshot[0];
        AssertEx.Equal(NodeChatMessagePartKinds.Notice, part.Kind);
        AssertEx.Equal(nameof(TurnNoticeKind.HistoryTruncated), part.Name);
        AssertEx.Equal(payload.Message, part.Text);
        AssertEx.Equal(expected: 9, part.Sequence);
    }

    private static ToolCallLifecyclePayload NewPayload(string toolCallId,
        string toolName,
        ToolCallLifecyclePhase phase,
        string? arguments = null,
        bool requiresApproval = false,
        string? result = null,
        bool isError = false)
    {
        return new ToolCallLifecyclePayload
        {
            InvocationId = Guid.NewGuid(),
            ToolCallId = toolCallId,
            ToolName = toolName,
            Phase = phase,
            Arguments = arguments,
            RequiresApproval = requiresApproval,
            Result = result,
            IsError = isError
        };
    }

    private static NodeChatPersistedMessageDto NewMessage(NodeChatMessageCorrelation correlation,
        string content,
        string? reasoning,
        string status,
        string? model,
        int? inputCount,
        int? outputCount)
    {
        return new NodeChatPersistedMessageDto(correlation.MessageId,
            correlation.ConversationId,
            correlation.RequestId,
            Sequence: 1,
            Role: "assistant",
            Content: content,
            Reasoning: reasoning,
            Status: status,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0,
            Model: model,
            Error: null,
            MetadataJson: null,
            InputCount: inputCount,
            OutputCount: outputCount);
    }
}
