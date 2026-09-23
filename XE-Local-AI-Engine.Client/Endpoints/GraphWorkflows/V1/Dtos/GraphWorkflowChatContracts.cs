namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

/// <summary>
///     A chat message into workflow mode. <see cref="RequestId" /> is the caller-minted idempotency key: the same one
///     always answers with the same run and message id, and completes a send that was cut off half way.
/// </summary>
public sealed class SendGraphWorkflowChatMessageRequest
{
    public Guid ConversationId { get; init; }

    public Guid RequestId { get; init; }

    /// <summary>The Chat workflow the composer has selected. Must be a <c>Chat</c> definition.</summary>
    public Guid DefinitionId { get; init; }

    public string Content { get; init; } = string.Empty;

    /// <summary>Conversation upload ids; refused unless the workflow's <c>chat.acceptsAttachments</c> is on.</summary>
    public IReadOnlyList<Guid>? AttachmentFileIds { get; init; }

    /// <summary>Set after a 409 <c>GraphWorkflowRerunConfirmationRequired</c>, when the user confirmed another run.</summary>
    public bool? ConfirmRerun { get; init; }
}

public sealed class SendGraphWorkflowChatMessageResponse
{
    public required Guid RunId { get; init; }

    /// <summary>The user message the send persisted, deterministic in the request id.</summary>
    public required Guid MessageId { get; init; }

    /// <summary><c>started</c> or <c>answered</c>.</summary>
    public required string Action { get; init; }
}

public sealed class ListGraphWorkflowConversationRunsRequest
{
    public Guid ConversationId { get; init; }

    public int Limit { get; init; } = 20;
}

/// <summary>The run's parked ChatInput: the chat composer's next send answers it.</summary>
public sealed class GraphWorkflowPendingInputResponse
{
    public required string NodeKey { get; init; }

    public required string Prompt { get; init; }
}

/// <summary>The Agent or LLM call node whose row is queued or running. Steering it is a later route; this tells the UI which.</summary>
public sealed class GraphWorkflowSteerableNodeResponse
{
    public required string NodeKey { get; init; }
}

/// <summary>One run bound to a conversation, with what the chat page renders it from.</summary>
public sealed class GraphWorkflowConversationRunResponse
{
    public required GraphWorkflowRunSummaryResponse Run { get; init; }

    public required Guid DefinitionId { get; init; }

    /// <summary>Null when the definition was deleted since.</summary>
    public required string? DefinitionName { get; init; }

    /// <summary>The user message that started the run; the chat page renders the run's activity after it.</summary>
    public required Guid? TriggerMessageId { get; init; }

    public required GraphWorkflowPendingInputResponse? PendingInput { get; init; }

    public required GraphWorkflowSteerableNodeResponse? Steerable { get; init; }
}

public sealed class ListGraphWorkflowConversationRunsResponse
{
    public required IReadOnlyList<GraphWorkflowConversationRunResponse> Runs { get; init; }
}
