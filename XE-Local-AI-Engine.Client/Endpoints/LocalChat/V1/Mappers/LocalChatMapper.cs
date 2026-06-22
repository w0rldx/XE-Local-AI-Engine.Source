namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.Chat;

internal static class LocalChatMapper
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
            Origin = conversation.Origin,
            IsPinned = conversation.IsPinned,
            Archived = conversation.Archived,
            BranchOfConversationId = conversation.BranchOfConversationId,
            SelectedPath = conversation.SelectedPath,
            MemoryExcluded = conversation.MemoryExcluded,
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
            Purged = summary.Purged,
            Origin = summary.Origin,
            IsPinned = summary.IsPinned,
            Archived = summary.Archived
        };
    }

    public static NodeChatBranchConversationResponse ToResponse(this NodeChatBranchResultDto branch)
    {
        ArgumentNullException.ThrowIfNull(branch);

        return new NodeChatBranchConversationResponse
        {
            SourceConversationId = branch.SourceConversationId,
            BranchedConversationId = branch.BranchedConversationId,
            CopiedMessageCount = branch.CopiedMessageCount
        };
    }

    public static NodeChatMessageFeedbackResponse ToResponse(this NodeChatMessageFeedbackDto feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);

        return new NodeChatMessageFeedbackResponse
        {
            MessageId = feedback.MessageId,
            ConversationId = feedback.ConversationId,
            Rating = feedback.Rating,
            Comment = feedback.Comment,
            CreatedAtUtc = feedback.CreatedAtUtc,
            UpdatedAtUtc = feedback.UpdatedAtUtc
        };
    }

    public static NodeChatMessageResponse ToResponse(this NodeChatPersistedMessageDto message)
    {
        ArgumentNullException.ThrowIfNull(message);

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
            Origin = message.Origin,
            Model = message.Model,
            Error = message.Error,
            InputTokens = message.InputCount,
            OutputTokens = message.OutputCount,
            TotalTokens = message.TotalCount,
            ReasoningTokens = message.ReasoningCount,
            ParentMessageId = message.ParentMessageId,
            VariantGroupId = message.VariantGroupId,
            FeedbackRating = message.FeedbackRating,
            FeedbackComment = message.FeedbackComment,
            // Surface the persisted ordered interleave verbatim; null for legacy messages (client synthesizes from
            // Reasoning). The DTO part records carry no transport-specific shape, so they pass through unchanged.
            Parts = message.Parts,
            // Per-response agent attribution snapshot (null for legacy turns + user messages; the client renders the
            // localized "Default Assistant" fallback when AgentName is null).
            AgentDefinitionId = message.AgentDefinitionId,
            AgentName = message.AgentName,
            // The reasoning effort used to generate this turn (null for legacy turns + user messages).
            ReasoningEffort = message.ReasoningEffort,
            // Whole-turn generation duration (null for legacy turns + user messages); drives the optional tps display.
            GenerationDurationMs = message.GenerationDurationMs
        };
    }
}
