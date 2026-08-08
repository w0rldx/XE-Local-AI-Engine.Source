namespace XE_Local_AI_Engine.Client.Services.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Startup background service that re-derives and re-encrypts conversation titles after the
///     <c>EncryptConversationTitle</c> migration, which NULLs all existing plaintext titles because migrations cannot
///     access the node encryption key. For each conversation whose title is NULL, this service reads the first
///     user-role message, derives the title via <see cref="NodeChatTitle.FromUserContent" />, encrypts it with the
///     node key, and writes it back. Runs once per startup; safe to re-run (conversations without a user message are
///     left NULL and processed again on the next restart until a message arrives).
/// </summary>
public sealed class NodeChatTitleEncryptionBackfillService(
    IServiceScopeFactory scopeFactory,
    ILogger<NodeChatTitleEncryptionBackfillService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

            // Find all non-purged conversations with a NULL title. These are rows that existed before the
            // EncryptConversationTitle migration (which NULLs plaintext titles) or conversations created before
            // the first user message arrived.
            var conversationIds = await dbContext.Database
                                                 .SqlQueryRaw<Guid>("SELECT conversation_id FROM conversations WHERE purged = 0 AND title IS NULL")
                                                 .ToListAsync(stoppingToken)
                                                 .ConfigureAwait(false);

            if (conversationIds.Count == 0)
            {
                return;
            }

            logger.LogInformation("NodeChatTitleEncryptionBackfillService: backfilling encrypted titles for {Count} conversation(s).",
                conversationIds.Count);

            var backfilled = 0;
            foreach (var conversationId in conversationIds)
            {
                stoppingToken.ThrowIfCancellationRequested();

                // Read the first user-role message id + content blob. DecryptMessageContent is read-both, so this
                // recovers the plaintext whether the row is a legacy plaintext blob or an encrypted envelope
                // (content AAD = conversationId + messageId + "content").
                var row = await dbContext.Database
                                         .SqlQueryRaw<MessageIdAndContent>(
                                             "SELECT message_id AS MessageId, content AS Content FROM messages WHERE conversation_id = {0} AND role = 'user' ORDER BY sequence ASC LIMIT 1",
                                             conversationId)
                                         .FirstOrDefaultAsync(stoppingToken)
                                         .ConfigureAwait(false);

                if (row is null)
                {
                    // No user message yet — leave title NULL; it will be set when the first message arrives.
                    continue;
                }

                string contentText;
                try
                {
                    contentText = dbContext.DecryptMessageContent(row.Content, conversationId, row.MessageId);
                }
                catch (Exception ex)
                {
                    // Read-both means a plaintext row can no longer land here; a throw now indicates a genuinely
                    // undecryptable envelope (e.g. ciphertext written under a previous node key). Skip that row only.
                    logger.LogWarning(
                        "NodeChatTitleEncryptionBackfillService: could not decrypt message content for conversation {ConversationId}; skipping (row likely encrypted under a previous node key). [{ErrorType}]",
                        conversationId,
                        ex.GetType().Name);
                    continue;
                }

                var title = NodeChatTitle.FromUserContent(contentText);
                var encryptedTitle = dbContext.EncryptConversationTitle(title, conversationId);

                await dbContext.Database
                               .ExecuteSqlRawAsync("UPDATE conversations SET title = {0} WHERE conversation_id = {1}",
                                   [encryptedTitle is null ? DBNull.Value : encryptedTitle, conversationId],
                                   stoppingToken)
                               .ConfigureAwait(false);

                backfilled++;
            }

            logger.LogInformation("NodeChatTitleEncryptionBackfillService: backfill complete — {Backfilled} title(s) encrypted, {Skipped} skipped (no user message yet).",
                backfilled,
                conversationIds.Count - backfilled);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — do not log as error.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NodeChatTitleEncryptionBackfillService: unexpected error during title backfill.");
        }
    }

    // Minimal projection for reading message id + encrypted content blob from a raw SQL query.
    private sealed record MessageIdAndContent(Guid MessageId, byte[] Content);
}
