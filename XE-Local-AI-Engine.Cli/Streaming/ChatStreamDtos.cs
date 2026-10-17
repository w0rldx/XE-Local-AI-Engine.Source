namespace XE_Local_AI_Engine.Cli.Streaming;

// CLI copies of the hub records in Client.Application/Services/Chat/NodeChatStreamDtos.cs, with only the members the
// CLI uses. ChatStreamDtoContractTests pins every name and type to the server record.

/// <summary>The <c>LocalChatHub.SendMessage</c> argument; a record so <c>ChatSession</c> can fill the ids with <c>with</c>.</summary>
internal sealed record NodeChatStreamRequest
{
    public Guid ConversationId { get; init; }

    public string Content { get; init; } = string.Empty;

    /// <summary>The assistant message id; minted by <c>ChatSession.SendAsync</c> when absent.</summary>
    public Guid? MessageId { get; init; }

    /// <summary>The invocation id cancel and resume key on; minted by <c>ChatSession.SendAsync</c> when absent.</summary>
    public Guid? RequestId { get; init; }

    public Guid? UserMessageId { get; init; }

    public string? Model { get; init; }

    public bool UseLocalTools { get; init; }

    public string? ReasoningEffort { get; init; }

    public Guid? AgentDefinitionId { get; init; }
}

/// <summary>One hub stream frame. Read back from the hub, so no member is <c>required</c>.</summary>
internal sealed class ChatStreamEvent
{
    public string Type { get; init; } = string.Empty;

    public Guid ConversationId { get; init; }

    public Guid MessageId { get; init; }

    public Guid RequestId { get; init; }

    public string Status { get; init; } = string.Empty;

    public long Sequence { get; init; }

    public long OccurredAtUtc { get; init; }

    public string? Delta { get; init; }

    public string? ReasoningDelta { get; init; }

    public string? Content { get; init; }

    public string? Reasoning { get; init; }

    public string? Error { get; init; }

    public string? Model { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public int? TotalTokens { get; init; }

    public int? ReasoningTokens { get; init; }

    public string? ToolCallId { get; init; }

    public string? ToolName { get; init; }

    public string? Arguments { get; init; }

    public bool? RequiresApproval { get; init; }

    public string? Result { get; init; }

    public bool? IsError { get; init; }

    public string? NoticeKind { get; init; }

    public string? NoticeMessage { get; init; }

    public string? NoticeDetail { get; init; }

    public string? RuntimePhase { get; init; }

    public string? ApprovalRequestId { get; init; }

    public string? QuestionRequestId { get; init; }

    /// <summary>On <c>question-requested</c>: the questions as the node's JSON text.</summary>
    public string? Questions { get; init; }

    public long? ContentOffset { get; init; }

    public long? ReasoningOffset { get; init; }

    public int? InvocationTimeoutSeconds { get; init; }

    public bool? SessionScopeEligible { get; init; }
}
