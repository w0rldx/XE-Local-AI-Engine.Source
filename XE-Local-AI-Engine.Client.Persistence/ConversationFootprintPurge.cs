namespace XE_Local_AI_Engine.Client.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
///     Single source of truth for the complete DB footprint of a conversation.
/// </summary>
/// <remarks>
///     Most of the footprint is keyed by conversation id with NO foreign key on purpose, so those tables go only when
///     this list names them or their rows orphan — a privacy gap: docs/wiki/08-data-and-persistence.md ("Purging a
///     conversation's footprint"). Deletes DB rows only; the caller owns the enclosing transaction and any on-disk
///     upload-blob teardown, since the encrypted upload bytes and cached extracted text live on disk, not in a
///     column. Deleting a conversation whose rows are already gone is a harmless no-op.
/// </remarks>
public static class ConversationFootprintPurge
{
    /// <summary>
    ///     Every table keyed by <c>conversation_id</c> (or <c>message_id</c>) that <see cref="DeleteAsync" /> deletes
    ///     from, excluding the root <c>conversations</c> table itself.
    /// </summary>
    /// <remarks>
    ///     Exists so a test in <c>XE_Local_AI_Engine.Client.Persistence.Tests</c> can enumerate every
    ///     conversation/message-keyed table in the EF model and assert it appears here, catching the exact drift this
    ///     class warns about. Whenever a <c>DELETE FROM</c> statement below is added, removed or changed, update this
    ///     list to match.
    /// </remarks>
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
    ///     Tables that carry a conversation binding the purge NULLS instead of deleting: history that outlives its
    ///     conversation. Listed beside <see cref="CoveredChildTables" /> so the coverage test can tell them from an omission.
    /// </summary>
    internal static readonly IReadOnlyList<string> UnboundChildTables =
    [
        "graph_workflow_runs"
    ];

    /// <summary>
    ///     Deletes every child row and the conversation row for <paramref name="conversationId" /> on
    ///     <paramref name="dbContext" />'s connection. Runs within the caller's transaction; the conversation row is
    ///     deleted last.
    /// </summary>
    public static async Task DeleteAsync(NodeChatDbContext dbContext, Guid conversationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM message_feedback WHERE conversation_id = {0};", [conversationId], cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM messages WHERE conversation_id = {0};", [conversationId], cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM tool_events WHERE conversation_id = {0};", [conversationId], cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM conversation_uploaded_files WHERE conversation_id = {0};", [conversationId], cancellationToken);
        // Execution-log telemetry carries plaintext conversation/message correlation ids on both record kinds. Without this delete they would survive an immediate
        // conversation purge for the separate execution-log retention period — a privacy gap. Deleting on conversation_id covers both.
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM agent_execution_logs WHERE conversation_id = {0};", [conversationId], cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM purged_tombstones WHERE conversation_id = {0};", [conversationId], cancellationToken);

        // A work session owns its conversation, so purging it takes the session and its whole subtree. Only agent_work_sessions carries conversation_id, so the five
        // child tables resolve through a subselect on it and must go FIRST. Why, and what the caller still owes: docs/wiki/08-data-and-persistence.md.
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM agent_work_session_events WHERE session_id IN (SELECT id FROM agent_work_sessions WHERE conversation_id = {0});",
            [conversationId],
            cancellationToken);
        await dbContext.Database
                       .ExecuteSqlRawAsync("DELETE FROM agent_work_session_checkpoints WHERE session_id IN (SELECT id FROM agent_work_sessions WHERE conversation_id = {0});",
                           [conversationId],
                           cancellationToken);
        await dbContext.Database
                       .ExecuteSqlRawAsync("DELETE FROM agent_work_session_artifacts WHERE session_id IN (SELECT id FROM agent_work_sessions WHERE conversation_id = {0});",
                           [conversationId],
                           cancellationToken);
        await dbContext.Database
                       .ExecuteSqlRawAsync("DELETE FROM agent_work_session_findings WHERE session_id IN (SELECT id FROM agent_work_sessions WHERE conversation_id = {0});",
                           [conversationId],
                           cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM agent_work_session_tasks WHERE session_id IN (SELECT id FROM agent_work_sessions WHERE conversation_id = {0});",
            [conversationId],
            cancellationToken);

        // An integration session owns its conversation on the same terms, so the two descendant tables resolve through a subselect on integration_sessions and must
        // go FIRST. integration_triggers and integration_api_keys are node-scoped and are correctly untouched by a conversation purge.
        await dbContext.Database
                       .ExecuteSqlRawAsync(
                           "DELETE FROM integration_execution_events WHERE execution_id IN (SELECT e.id FROM integration_executions e JOIN integration_sessions s ON s.id = e.session_id WHERE s.conversation_id = {0});",
                           [conversationId],
                           cancellationToken);
        await dbContext.Database
                       .ExecuteSqlRawAsync("DELETE FROM integration_executions WHERE session_id IN (SELECT id FROM integration_sessions WHERE conversation_id = {0});",
                           [conversationId],
                           cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM integration_sessions WHERE conversation_id = {0};", [conversationId], cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM agent_work_sessions WHERE conversation_id = {0};", [conversationId], cancellationToken);

        // A graph workflow run is an audit that outlives its chat: unbound, never deleted. Written out rather than left to the
        // foreign key's SET NULL, so the unbinding holds on a connection whatever its foreign-key pragma says.
        await dbContext.Database.ExecuteSqlRawAsync("UPDATE graph_workflow_runs SET conversation_id = NULL WHERE conversation_id = {0};", [conversationId], cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM conversations WHERE conversation_id = {0};", [conversationId], cancellationToken);
    }
}
