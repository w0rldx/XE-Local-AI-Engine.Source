namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Chat;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>One chat send into workflow mode. <see cref="RequestId" /> is the caller-minted idempotency key.</summary>
public sealed class GraphWorkflowChatSendRequest
{
    public required Guid RequestId { get; init; }

    public required Guid DefinitionId { get; init; }

    public required string Content { get; init; }

    public IReadOnlyList<Guid> AttachmentFileIds { get; init; } = [];

    /// <summary>The user confirmed starting another run where an earlier one already ran.</summary>
    public bool ConfirmRerun { get; init; }
}

/// <summary>What a send did: started a run, or answered the run's parked <c>ChatInput</c>.</summary>
public enum GraphWorkflowChatSendAction
{
    Started,
    Answered
}

public sealed class GraphWorkflowChatSendResult
{
    public required Guid RunId { get; init; }

    /// <summary>The user message the send persisted — deterministic in the request id, so a replay names the same one.</summary>
    public required Guid MessageId { get; init; }

    public required GraphWorkflowChatSendAction Action { get; init; }
}

/// <summary>The run's parked <c>ChatInput</c>: what the chat composer answers next.</summary>
public sealed class GraphWorkflowChatPendingInput
{
    public required string NodeKey { get; init; }

    public required string Prompt { get; init; }
}

/// <summary>A run bound to a conversation, with what the chat page needs to render it after a reload.</summary>
public sealed class GraphWorkflowChatBoundRun
{
    public required GraphWorkflowRunSnapshot Run { get; init; }

    /// <summary>Null when the definition was deleted: runs carry no foreign key to it.</summary>
    public required string? DefinitionName { get; init; }

    public required GraphWorkflowChatPendingInput? PendingInput { get; init; }

    /// <summary>The Agent or LLM call node whose row is queued or running — the one a steer would target.</summary>
    public required string? SteerableNodeKey { get; init; }
}

/// <summary>A send that would start another run where the last one asked to be confirmed first. Nothing was persisted.</summary>
public sealed class GraphWorkflowRerunConfirmationRequiredException : InvalidOperationException
{
    public GraphWorkflowRerunConfirmationRequiredException(string message) : base(message)
    {
    }
}

/// <summary>A send carrying attachments to a workflow — or an input request — that does not take them.</summary>
public sealed class GraphWorkflowAttachmentsNotAcceptedException : InvalidOperationException
{
    public GraphWorkflowAttachmentsNotAcceptedException(string message) : base(message)
    {
    }
}

/// <summary>The chat surface over graph workflows: send into a conversation's workflow, list its bound runs.</summary>
/// <remarks>
///     A send validates, looks for a replay by request id, then either starts a bound run or answers the bound run's
///     parked <c>ChatInput</c>; every other live state is busy. Refusals: 404 for an unknown definition or conversation,
///     400 for a request that is wrong, 409 for a conversation whose state forbids it.
/// </remarks>
public interface IGraphWorkflowChatService
{
    /// <param name="decidedBySubject">Who answers a parked input — the same audit a decide records.</param>
    Task<GraphWorkflowChatSendResult> SendAsync(Guid conversationId,
        GraphWorkflowChatSendRequest request,
        string? decidedBySubject,
        CancellationToken cancellationToken = default);

    /// <summary>The conversation's bound runs, newest first.</summary>
    Task<IReadOnlyList<GraphWorkflowChatBoundRun>> ListBoundRunsAsync(Guid conversationId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Cancels the conversation's live bound run, if it has one — what a conversation delete does first.</summary>
    Task CancelBoundRunAsync(Guid conversationId, CancellationToken cancellationToken = default);
}
