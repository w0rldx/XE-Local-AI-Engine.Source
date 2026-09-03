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
    ///     Every table keyed by <c>conversation_id</c> (or <c>message_id</c>) that <see cref="DeleteAsync" /> deletes
    ///     from, excluding the root <c>conversations</c> table itself. Exists so a test (BE-08, in
    ///     XE_Local_AI_Engine.Client.Persistence.Tests) can enumerate every conversation/message-keyed table in the EF
    ///     model and assert it appears here — catching the exact drift this class's remarks warn about. Whenever a
    ///     <c>DELETE FROM</c> statement below is added, remove, or changed, update this list to match.
    /// </summary>
    internal static readonly IReadOnlyList<string> CoveredChildTables =
    [
        "message_feedback",
        "messages",
        "tool_events",
        "conversation_uploaded_files",
        "agent_execution_logs",
        "purged_tombstones",
        "agent_work_sessions",
        "integration_sessions"
    ];

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
        // Execution-log telemetry carries plaintext conversation/message correlation ids (both the adaptive-memory
        // diagnostics rows and the durable run envelopes). Without this delete those correlations would survive an
        // immediate conversation purge for the separate execution-log retention period — a privacy gap. Covers both
        // record kinds by deleting on conversation_id.
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM agent_execution_logs WHERE conversation_id = {0};", [conversationId], cancellationToken).ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM purged_tombstones WHERE conversation_id = {0};", [conversationId], cancellationToken).ConfigureAwait(false);

        // A work session owns its conversation, so purging the conversation takes the session and its whole subtree with
        // it: the objective, the plan, the findings and the checkpoints are all conversation-derived encrypted content,
        // and leaving them keyed to a purged conversation is the privacy gap this class exists to close. Only
        // agent_work_sessions carries conversation_id, so the five child tables resolve through a subselect on it and
        // must go FIRST — once the session row is gone the subselect no longer finds them. They are deliberately absent
        // from CoveredChildTables, which mirrors what BE-08 discovers: conversation/message-keyed tables only.
        //
        // A session's artifact bytes live encrypted on disk under work-sessions/artifacts/{sessionId:N}/. This row purge
        // does not remove those files or upload blobs; the caller owns both teardown paths.
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM agent_work_session_events WHERE session_id IN (SELECT id FROM agent_work_sessions WHERE conversation_id = {0});",
                           [conversationId],
                           cancellationToken)
                       .ConfigureAwait(false);
        await dbContext.Database
                       .ExecuteSqlRawAsync("DELETE FROM agent_work_session_checkpoints WHERE session_id IN (SELECT id FROM agent_work_sessions WHERE conversation_id = {0});",
                           [conversationId],
                           cancellationToken)
                       .ConfigureAwait(false);
        await dbContext.Database
                       .ExecuteSqlRawAsync("DELETE FROM agent_work_session_artifacts WHERE session_id IN (SELECT id FROM agent_work_sessions WHERE conversation_id = {0});",
                           [conversationId],
                           cancellationToken)
                       .ConfigureAwait(false);
        await dbContext.Database
                       .ExecuteSqlRawAsync("DELETE FROM agent_work_session_findings WHERE session_id IN (SELECT id FROM agent_work_sessions WHERE conversation_id = {0});",
                           [conversationId],
                           cancellationToken)
                       .ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM agent_work_session_tasks WHERE session_id IN (SELECT id FROM agent_work_sessions WHERE conversation_id = {0});",
                           [conversationId],
                           cancellationToken)
                       .ConfigureAwait(false);

        // An integration session owns its conversation on the same terms, so purging the conversation takes the
        // session, its executions and their events with it. Only integration_sessions carries conversation_id, so
        // the two descendant tables resolve through a subselect on it and must go FIRST — once the session row is
        // gone the subselect no longer finds them. They are deliberately absent from CoveredChildTables, which
        // mirrors what BE-08 discovers: conversation/message-keyed tables only. integration_triggers and
        // integration_api_keys are node-scoped and are correctly untouched by a conversation purge.
        await dbContext.Database
                       .ExecuteSqlRawAsync(
                           "DELETE FROM integration_execution_events WHERE execution_id IN (SELECT e.id FROM integration_executions e JOIN integration_sessions s ON s.id = e.session_id WHERE s.conversation_id = {0});",
                           [conversationId],
                           cancellationToken)
                       .ConfigureAwait(false);
        await dbContext.Database
                       .ExecuteSqlRawAsync("DELETE FROM integration_executions WHERE session_id IN (SELECT id FROM integration_sessions WHERE conversation_id = {0});",
                           [conversationId],
                           cancellationToken)
                       .ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM integration_sessions WHERE conversation_id = {0};", [conversationId], cancellationToken).ConfigureAwait(false);

        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM agent_work_sessions WHERE conversation_id = {0};", [conversationId], cancellationToken).ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM conversations WHERE conversation_id = {0};", [conversationId], cancellationToken).ConfigureAwait(false);
    }
}
