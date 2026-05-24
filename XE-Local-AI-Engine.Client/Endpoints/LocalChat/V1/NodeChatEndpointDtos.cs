namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class CreateNodeChatConversationRequest
{
    public string? Title { get; init; }

    public string? UserId { get; init; }
}

public sealed class ListNodeChatConversationsRequest
{
    public bool IncludeArchived { get; init; }

    public int? Limit { get; init; }
}

public sealed class ListNodeChatConversationsResponse
{
    public required IReadOnlyList<NodeChatConversationSummaryResponse> Items { get; init; }
}

public sealed class GetNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }
}

public sealed class DeleteNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }

    public bool PurgeImmediately { get; init; }
}

public sealed class CancelNodeChatMessageRequest
{
    public Guid ConversationId { get; init; }

    public Guid MessageId { get; init; }

    public Guid RequestId { get; init; }
}

public sealed class NodeChatConversationSummaryResponse
{
    public required Guid ConversationId { get; init; }

    public string? Title { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long LastSeenUtc { get; init; }

    public string? LastMessagePreview { get; init; }

    public string? LastMessageStatus { get; init; }

    public required bool Purged { get; init; }
}

public sealed class NodeChatConversationResponse
{
    public required Guid ConversationId { get; init; }

    public string? Title { get; init; }

    public string? UserId { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long LastSeenUtc { get; init; }

    public required bool Purged { get; init; }

    public required IReadOnlyList<NodeChatMessageResponse> Messages { get; init; }
}

public sealed class NodeChatMessageResponse
{
    public required Guid MessageId { get; init; }

    public required Guid ConversationId { get; init; }

    public Guid? RequestId { get; init; }

    public required int Sequence { get; init; }

    public required string Role { get; init; }

    public required string Content { get; init; }

    public string? Reasoning { get; init; }

    public required string Status { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public string? Model { get; init; }

    public string? Error { get; init; }
}

public sealed class NodeChatCancelMessageResponse
{
    public required Guid ConversationId { get; init; }

    public required Guid MessageId { get; init; }

    public required Guid RequestId { get; init; }

    public required string Status { get; init; }

    public required bool Cancelled { get; init; }
}

public sealed class NodeChatDeleteConversationResponse
{
    public required Guid ConversationId { get; init; }

    public required bool CancelRequested { get; init; }

    public required bool Purged { get; init; }
}

internal static class NodeChatEndpointDtoMapper
{
    public static NodeChatConversationResponse ToResponse(this NodeChatConversationDto conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        return new NodeChatConversationResponse
        {
            ConversationId = conversation.ConversationId,
            Title = conversation.Title,
            UserId = conversation.UserId,
            CreatedAtUtc = conversation.CreatedAtUtc,
            LastSeenUtc = conversation.LastSeenUtc,
            Purged = conversation.Purged,
            Messages = conversation.Messages.Select(static message => message.ToResponse()).ToArray()
        };
    }

    public static NodeChatConversationSummaryResponse ToResponse(this NodeChatConversationSummaryDto summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new NodeChatConversationSummaryResponse
        {
            ConversationId = summary.ConversationId,
            Title = summary.Title,
            CreatedAtUtc = summary.CreatedAtUtc,
            LastSeenUtc = summary.LastSeenUtc,
            LastMessagePreview = summary.LastMessagePreview,
            LastMessageStatus = summary.LastMessageStatus,
            Purged = summary.Purged
        };
    }

    private static NodeChatMessageResponse ToResponse(this NodeChatPersistedMessageDto message)
    {
        return new NodeChatMessageResponse
        {
            MessageId = message.MessageId,
            ConversationId = message.ConversationId,
            RequestId = message.RequestId,
            Sequence = message.Sequence,
            Role = message.Role,
            Content = message.Content,
            Reasoning = message.Reasoning,
            Status = message.Status,
            CreatedAtUtc = message.CreatedAtUtc,
            UpdatedAtUtc = message.UpdatedAtUtc,
            Model = message.Model,
            Error = message.Error
        };
    }
}
