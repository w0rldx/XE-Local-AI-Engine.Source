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
            AssertEx.Equal(live with
            {
                MessageId = invocationId,
                RequestId = invocationId
            }, resume);
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
    public void MessageEvent_OnATerminal_StillCarriesTheFullContent()
    {
        // A terminal is one frame per turn, so carrying the whole message on it costs nothing — and it is the backstop
        // that converges a client whose delta stream fell behind. Only the DELTA path lost its content.
        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var message = NewMessage(correlation, content: "the whole answer", reasoning: "the whole reasoning", status: NodeChatMessageStatusValues.Completed, model: "model-x", inputCount: 1,
            outputCount: 2);

        var terminal = ChatStreamEventMapper.MessageEvent(ChatStreamEventTypes.AssistantCompleted, correlation, message, Timestamp, sequence: 3);

        AssertEx.Equal("the whole answer", terminal.Content);
        AssertEx.Equal("the whole reasoning", terminal.Reasoning);
        // A terminal is not a delta: it carries no increment and no offsets to continue from.
        AssertEx.Null(terminal.Delta);
        AssertEx.Null(terminal.ReasoningDelta);
        AssertEx.Null(terminal.ContentOffset);
        AssertEx.Null(terminal.ReasoningOffset);
    }

    [Test]
    public void DeltaEvent_CarriesOnlyTheIncrementAndItsOffsets()
    {
        // The load-bearing assertion of the delta-only protocol: a live frame must never carry the accumulated text.
        // Populating Content here is exactly what made the wire cost of a turn quadratic in its output length, and the
        // client is built to APPEND this event rather than replace from it.
        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var delta = ChatStreamEventMapper.DeltaEvent(correlation, Timestamp, sequence: 8, contentDelta: " world", reasoningDelta: "…therefore", contentOffset: 5, reasoningOffset: 12);

        AssertEx.Equal(ChatStreamEventTypes.AssistantDelta, delta.Type);
        AssertEx.Equal(NodeChatMessageStatusValues.Streaming, delta.Status);
        AssertEx.Equal(correlation.ConversationId, delta.ConversationId);
        AssertEx.Equal(correlation.MessageId, delta.MessageId);
        AssertEx.Equal(correlation.RequestId, delta.RequestId);
        AssertEx.Equal(expected: 8L, delta.Sequence);
        AssertEx.Equal(Timestamp, delta.OccurredAtUtc);
        AssertEx.Equal(" world", delta.Delta);
        AssertEx.Equal("…therefore", delta.ReasoningDelta);
        AssertEx.Equal(expected: 5L, delta.ContentOffset);
        AssertEx.Equal(expected: 12L, delta.ReasoningOffset);

        AssertEx.Null(delta.Content);
        AssertEx.Null(delta.Reasoning);
        // Nor any of the row-derived fields a delta no longer has a row to read.
        AssertEx.Null(delta.Model);
        AssertEx.Null(delta.InputTokens);
        AssertEx.Null(delta.OutputTokens);
        AssertEx.Null(delta.Error);
    }

    [Test]
    public void DeltaEvent_WhenOnlyOneSideAdvanced_StillCarriesBothOffsets()
    {
        // A stalled side confirms its position rather than going silent, so the client's gap detector can tell
        // "reasoning did not advance" apart from "a reasoning delta was lost".
        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var delta = ChatStreamEventMapper.DeltaEvent(correlation, Timestamp, sequence: 1, contentDelta: "abc", reasoningDelta: null, contentOffset: 0, reasoningOffset: 40);

        AssertEx.Equal("abc", delta.Delta);
        AssertEx.Null(delta.ReasoningDelta);
        AssertEx.Equal(expected: 0L, delta.ContentOffset);
        AssertEx.Equal(expected: 40L, delta.ReasoningOffset);
    }

    [Test]
    public void SnapshotEvent_CarriesTheFullTextAndOffsetsEqualToItsLengths()
    {
        // The snapshot is the repair primitive: it replaces the client's accumulated text AND tells it where the next
        // delta will continue from, which is what lets one event serve resume replay, gap repair and overflow repair.
        var conversationId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();

        var snapshot = ChatStreamEventMapper.SnapshotEvent(conversationId, invocationId, invocationId, "Hello world", "I should greet them", Timestamp, sequence: 0);

        AssertEx.Equal(ChatStreamEventTypes.AssistantSnapshot, snapshot.Type);
        // Deliberately NOT terminal — the turn continues after a snapshot, so its status stays streaming.
        AssertEx.Equal(NodeChatMessageStatusValues.Streaming, snapshot.Status);
        AssertEx.Equal(conversationId, snapshot.ConversationId);
        AssertEx.Equal(invocationId, snapshot.MessageId);
        AssertEx.Equal(invocationId, snapshot.RequestId);
        AssertEx.Equal("Hello world", snapshot.Content);
        AssertEx.Equal("I should greet them", snapshot.Reasoning);
        AssertEx.Equal((long)"Hello world".Length, snapshot.ContentOffset);
        AssertEx.Equal((long)"I should greet them".Length, snapshot.ReasoningOffset);
        // A snapshot replaces; it never appends.
        AssertEx.Null(snapshot.Delta);
        AssertEx.Null(snapshot.ReasoningDelta);
    }

    [Test]
    public void SnapshotEvent_WithNoReasoning_ReportsAZeroReasoningOffset()
    {
        var snapshot = ChatStreamEventMapper.SnapshotEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Hi", reasoning: null, Timestamp, sequence: 0);

        AssertEx.Null(snapshot.Reasoning);
        AssertEx.Equal(expected: 0L, snapshot.ReasoningOffset);
        AssertEx.Equal(expected: 2L, snapshot.ContentOffset);
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
        // The payload's structured detail was computed at every emission site and then dropped here, so it was
        // observable nowhere — the adaptive-effort dispatch reason code, and equally the effective model the
        // withheld-attachment notices name.
        AssertEx.Equal("y", mapped.NoticeDetail);
        // A notice is not a tool-call event: none of the tool-specific fields are populated.
        AssertEx.Null(mapped.ToolCallId);
        AssertEx.Null(mapped.Result);
    }

    [Test]
    public void NoticeEvent_WhenThePayloadCarriesNoDetail_MapsANullDetail()
    {
        var payload = new TurnNoticePayload
        {
            InvocationId = Guid.NewGuid(),
            Kind = TurnNoticeKind.HistoryTruncated,
            Message = "Conversation history was trimmed to fit the model's context window."
        };

        var mapped = ChatStreamEventMapper.NoticeEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), payload, Timestamp, sequence: 4);

        AssertEx.Null(mapped.NoticeDetail);
    }

    [Test]
    public void ApprovalRequestedEvent_CarriesCallIdToolNameAndApprovalRequestId()
    {
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var payload = new ApprovalLifecyclePayload
        {
            InvocationId = Guid.NewGuid(),
            RequestId = "approval-abc",
            CallId = "call-9",
            ToolName = "search_web",
            Description = "A tool call (call-9) requires approval before it runs."
        };

        var mapped = ChatStreamEventMapper.ApprovalRequestedEvent(conversationId, messageId, requestId, payload, Timestamp, sequence: 12);

        AssertEx.Equal(ChatStreamEventTypes.ApprovalRequested, mapped.Type);
        AssertEx.Equal(NodeChatMessageStatusValues.Streaming, mapped.Status);
        AssertEx.Equal(conversationId, mapped.ConversationId);
        AssertEx.Equal(messageId, mapped.MessageId);
        AssertEx.Equal(requestId, mapped.RequestId);
        AssertEx.Equal(expected: 12L, mapped.Sequence);
        AssertEx.Equal(Timestamp, mapped.OccurredAtUtc);
        // The tool-call id rides on ToolCallId so the browser can attach the approve and deny controls to the
        // matching card, while the approval request id rides on its own field apart from the turn correlation id.
        AssertEx.Equal("call-9", mapped.ToolCallId);
        AssertEx.Equal("search_web", mapped.ToolName);
        AssertEx.Equal("approval-abc", mapped.ApprovalRequestId);
        // A pending approval carries no answer content and is not a completed tool result.
        AssertEx.Null(mapped.Result);
        AssertEx.Null(mapped.Delta);
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
        // No detail on the payload, so nothing is stored in the member a notice part reuses for it.
        AssertEx.Null(part.State);
    }

    [Test]
    public void AccumulateNotice_CarriesTheDetailOntoThePersistedPart()
    {
        // Live and reload must render the same notice. A notice part reuses the generic `State` member for its
        // structured detail the way it reuses `Name` for the notice kind, so the reason code survives a reload with
        // no new field on the persisted blob and no migration.
        var parts = new NodeChatPartAccumulator();
        var payload = new TurnNoticePayload
        {
            InvocationId = Guid.NewGuid(),
            Kind = TurnNoticeKind.EffortDispatched,
            Message = "Reasoning effort 'auto' resolved to Fast (low) for this turn.",
            Detail = "fast-model-unset"
        };

        ChatStreamEventMapper.AccumulateNotice(parts, payload, sequence: 3);

        var part = parts.Snapshot()[0];
        AssertEx.Equal(nameof(TurnNoticeKind.EffortDispatched), part.Name);
        AssertEx.Equal("fast-model-unset", part.State);
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

    [Test]
    public void QuestionRequestedEvent_CarriesCorrelationToolCallAndSerializedQuestions()
    {
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var payload = NewQuestionPayload("call-9", "ask_user", "question-abc");

        var mapped = ChatStreamEventMapper.QuestionRequestedEvent(conversationId, messageId, requestId, payload, Timestamp, sequence: 11);

        AssertEx.Equal(ChatStreamEventTypes.QuestionRequested, mapped.Type);
        // Status stays streaming: the turn is parked on the operator, not finished.
        AssertEx.Equal(NodeChatMessageStatusValues.Streaming, mapped.Status);
        AssertEx.Equal(conversationId, mapped.ConversationId);
        AssertEx.Equal(messageId, mapped.MessageId);
        AssertEx.Equal(requestId, mapped.RequestId);
        AssertEx.Equal(expected: 11L, mapped.Sequence);
        AssertEx.Equal(Timestamp, mapped.OccurredAtUtc);
        // The tool-call id is what the client attaches the question card to; the question request id is what it
        // echoes back to the resolve endpoint. They are distinct fields and must not be conflated.
        AssertEx.Equal("call-9", mapped.ToolCallId);
        AssertEx.Equal("ask_user", mapped.ToolName);
        AssertEx.Equal("question-abc", mapped.QuestionRequestId);
        // No content rides a question event.
        AssertEx.Null(mapped.Delta);
        AssertEx.Null(mapped.Content);
        AssertEx.Null(mapped.ApprovalRequestId);
    }

    [Test]
    public void QuestionRequestedEvent_SerializesQuestionsAsCamelCaseJson()
    {
        // The client parses this string to render the form, so the property names are a wire contract.
        var payload = NewQuestionPayload("call-1", "ask_user", "question-1");

        var mapped = ChatStreamEventMapper.QuestionRequestedEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), payload, Timestamp, sequence: 1);

        var questions = AssertEx.NotNull(mapped.Questions);
        AssertEx.Equal("[{\u0022header\u0022:\u0022Auth\u0022,\u0022question\u0022:\u0022Which auth method?\u0022,\u0022multiSelect\u0022:false,"
                       + "\u0022options\u0022:[{\u0022label\u0022:\u0022OAuth\u0022,\u0022description\u0022:\u0022Device flow\u0022,\u0022recommended\u0022:true},"
                       + "{\u0022label\u0022:\u0022API key\u0022,\u0022description\u0022:null,\u0022recommended\u0022:false}]}]",
            questions);
    }

    [Test]
    public void ApprovalRequestedEvent_WhenCallIdAndToolNameAreBlank_MapsThemToNull()
    {
        // The reconnect replay rebuilds this payload from InvocationApprovalState, whose CallId/ToolName are optional
        // (a platform-hub approval carries neither). A null tells the client "no card to attach this to"; an empty
        // string would look like a real id it could never match.
        var payload = new ApprovalLifecyclePayload
        {
            InvocationId = Guid.NewGuid(),
            RequestId = "approval-1",
            CallId = string.Empty,
            ToolName = string.Empty,
            Description = "Run a command"
        };

        var mapped = ChatStreamEventMapper.ApprovalRequestedEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), payload, Timestamp, sequence: 2);

        AssertEx.Null(mapped.ToolCallId);
        AssertEx.Null(mapped.ToolName);
        AssertEx.Equal("approval-1", mapped.ApprovalRequestId);
    }

    private static UserQuestionLifecyclePayload NewQuestionPayload(string callId, string toolName, string requestId)
    {
        return new UserQuestionLifecyclePayload
        {
            InvocationId = Guid.NewGuid(),
            RequestId = requestId,
            CallId = callId,
            ToolName = toolName,
            Questions =
            [
                new UserQuestionSpec("Auth",
                    "Which auth method?",
                    MultiSelect: false,
                    [
                        new UserQuestionOption("OAuth", "Device flow", Recommended: true),
                        new UserQuestionOption("API key", Description: null, Recommended: false)
                    ])
            ]
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
