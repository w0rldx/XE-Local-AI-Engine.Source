namespace XE_Local_AI_Engine.Client.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
///     Single source of truth for the complete DB footprint of a conversation. The node-sqlite runtime connection does
///     not enable <c>PRAGMA foreign_keys=ON</c>, so <c>ON DELETE CASCADE</c> never fires and every child table must be
///     deleted explicitly or its rows orphan (a privacy gap). Both the interactive immediate-purge path and the
///     retention sweeper delete through here so the table set can never drift between them.
/// </summary>
/// <remarks>
///     Deletes DB rows only; the caller owns the enclosing transaction and any on-disk upload-blob teardown (the
///     encrypted upload bytes and cached extracted text live on disk, not in a column). Deleting a conversation whose
///     rows are already gone is a harmless no-op, so the operation is idempotent.
/// </remarks>
public static class ConversationFootprintPurge
{
    /// <summary>
    ///     Deletes every child row and the conversation row for <paramref name="conversationId" /> on
    ///     <paramref name="dbContext" />'s connection. Runs within the caller's transaction; the conversation row is
    ///     deleted last.
    /// </summary>
    public static async Task DeleteAsync(NodeChatDbContext dbContext, Guid conversationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM message_feedback WHERE conversation_id = {0};", [conversationId], cancellationToken).ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM messages WHERE conversation_id = {0};", [conversationId], cancellationToken).ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM tool_events WHERE conversation_id = {0};", [conversationId], cancellationToken).ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM conversation_uploaded_files WHERE conversation_id = {0};", [conversationId], cancellationToken).ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM purged_tombstones WHERE conversation_id = {0};", [conversationId], cancellationToken).ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM conversations WHERE conversation_id = {0};", [conversationId], cancellationToken).ConfigureAwait(false);
    }
}
