namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using Microsoft.EntityFrameworkCore;
using static NodeChatPersistenceSql;

/// <summary>
///     Node-local message-feedback storage behind <see cref="NodeChatPersistenceService" />: the thumbs + optional
///     comment upsert and its read. One row per message; shares the single <see cref="NodeChatPersistenceWriter" />.
/// </summary>
internal sealed class NodeChatFeedbackStore(NodeChatPersistenceWriter writer)
{
    private readonly NodeChatPersistenceWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    public async Task<NodeChatMessageFeedbackDto> SetMessageFeedbackAsync(NodeChatSetMessageFeedbackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!NodeChatFeedbackRatingValues.All.Contains(request.Rating))
        {
            throw new ArgumentException($"Rating '{request.Rating}' is not a recognized feedback rating.", nameof(request));
        }

        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();

        return await _writer.ExecuteMessageUpdateAsync(request.ConversationId,
            request.MessageId,
            async (dbContext, token) =>
            {
                // Upsert keyed on the message id: re-submitting feedback overwrites rating/comment but preserves the
                // first-seen created_at_utc.
                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                                      INSERT INTO message_feedback (message_id, conversation_id, rating, comment, created_at_utc, updated_at_utc)
                                      VALUES ($message_id, $conversation_id, $rating, $comment, $created_at_utc, $updated_at_utc)
                                      ON CONFLICT(message_id) DO UPDATE SET
                                          conversation_id = excluded.conversation_id,
                                          rating = excluded.rating,
                                          comment = excluded.comment,
                                          updated_at_utc = excluded.updated_at_utc;
                                      """;
                AddParameter(command, "$message_id", request.MessageId);
                AddParameter(command, "$conversation_id", request.ConversationId);
                AddParameter(command, "$rating", request.Rating);
                AddParameter(command, "$comment", comment);
                AddParameter(command, "$created_at_utc", request.UpdatedAtUtc);
                AddParameter(command, "$updated_at_utc", request.UpdatedAtUtc);
                await OpenIfNeededAsync(command.Connection, token).ConfigureAwait(false);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                return await ReadFeedbackAsync(dbContext, request.ConversationId, request.MessageId, token).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("The message feedback row could not be persisted.");
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodeChatMessageFeedbackDto?> GetMessageFeedbackAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default)
    {
        return await _writer.ExecuteConversationSharedAsync(conversationId,
            (dbContext, token) => ReadFeedbackAsync(dbContext, conversationId, messageId, token),
            cancellationToken).ConfigureAwait(false);
    }
}
