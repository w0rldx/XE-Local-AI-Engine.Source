namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>InitialNodeChatSchema</c> is the floor every later chat migration alters: a conversation, its transcript, its
///     tool events, and the tombstones that outlive a purge. Both children cascade off <c>conversations</c>, which is
///     what makes deleting a conversation actually delete its transcript instead of stranding orphan rows carrying user
///     content. The columns are pinned as an exact set at this point in the chain, so a later migration that quietly
///     re-shapes the base tables fails here rather than in whichever suite happens to read them.
/// </summary>
public sealed class InitialNodeChatSchemaMigrationTests
{
    private const string ThisMigrationId = "20260419152305_InitialNodeChatSchema";

    [Test]
    public async Task Migrate_ToThisMigration_CreatesTheConversationTranscriptTables()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("initial-node-chat-schema.sqlite", ThisMigrationId).ConfigureAwait(false);

        foreach (var table in new[]
                 {
                     "conversations",
                     "messages",
                     "tool_events",
                     "purged_tombstones"
                 })
        {
            AssertEx.True(await probe.TableExistsAsync(table).ConfigureAwait(false), $"{table} must exist.");
        }

        AssertEx.True((await probe.ColumnsAsync("conversations").ConfigureAwait(false)).SetEquals(new[]
        {
            "conversation_id",
            "title",
            "user_id",
            "created_at_utc",
            "last_seen_utc",
            "purged"
        }), "conversations must expose exactly the columns this migration created.");

        AssertEx.True((await probe.ColumnsAsync("messages").ConfigureAwait(false)).SetEquals(new[]
        {
            "message_id",
            "conversation_id",
            "sequence",
            "role",
            "content",
            "metadata_json",
            "created_at_utc"
        }), "messages must expose exactly the columns this migration created.");

        AssertEx.True((await probe.ColumnsAsync("tool_events").ConfigureAwait(false)).SetEquals(new[]
        {
            "tool_call_id",
            "conversation_id",
            "tool_name",
            "plaintext_args",
            "plaintext_result",
            "status",
            "created_at_utc"
        }), "tool_events must expose exactly the columns this migration created.");

        AssertEx.True((await probe.ColumnsAsync("purged_tombstones").ConfigureAwait(false)).SetEquals(new[]
        {
            "conversation_id",
            "purged_at_utc",
            "acked_at_utc"
        }), "purged_tombstones must expose exactly the columns this migration created.");
    }

    [Test]
    public async Task Migrate_ToThisMigration_CascadesTheTranscriptOffTheConversation()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("initial-node-chat-cascade.sqlite", ThisMigrationId).ConfigureAwait(false);

        AssertEx.True(await probe.ForeignKeyExistsAsync("messages", "conversation_id", "conversations").ConfigureAwait(false),
            "Messages must be foreign-keyed to their conversation.");
        AssertEx.True(await probe.ForeignKeyExistsAsync("tool_events", "conversation_id", "conversations").ConfigureAwait(false),
            "Tool events must be foreign-keyed to their conversation.");

        // Deleting a conversation has to take the whole transcript with it — a stranded message row would keep user
        // content alive past the delete that was supposed to remove it.
        await probe.ExecuteAsync("PRAGMA foreign_keys = ON;").ConfigureAwait(false);
        await SeedTranscriptAsync(probe).ConfigureAwait(false);
        await probe.ExecuteAsync("DELETE FROM conversations;").ConfigureAwait(false);

        AssertEx.Equal(expected: 0L, (await probe.LongsAsync("SELECT COUNT(*) FROM messages;").ConfigureAwait(false)).Single(),
            "Deleting the conversation must cascade to its messages.");
        AssertEx.Equal(expected: 0L, (await probe.LongsAsync("SELECT COUNT(*) FROM tool_events;").ConfigureAwait(false)).Single(),
            "Deleting the conversation must cascade to its tool events.");
    }

    [Test]
    public async Task Migrate_ToThisMigration_IndexesTheTranscriptByConversation()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("initial-node-chat-indexes.sqlite", ThisMigrationId).ConfigureAwait(false);

        // Non-unique at this point in the chain; RepairAndUniqueMessageSequence later replaces the message index with a
        // unique (conversation_id, sequence) one, and that suite asserts the swap.
        AssertEx.True(await probe.IndexExistsAsync("messages", "IX_messages_conversation_id", unique: false, "conversation_id").ConfigureAwait(false),
            "Messages must be indexed by conversation.");
        AssertEx.True(await probe.IndexExistsAsync("tool_events", "IX_tool_events_conversation_id", unique: false, "conversation_id").ConfigureAwait(false),
            "Tool events must be indexed by conversation.");
    }

    private static async Task SeedTranscriptAsync(MigrationSchemaProbe probe)
    {
        var conversationId = Guid.NewGuid().ToString();

        await probe.ExecuteAsync("""
                                 INSERT INTO conversations (conversation_id, title, user_id, created_at_utc, last_seen_utc, purged)
                                 VALUES ($conversation_id, 'Historical', 'node', 1234, 1234, 0);
                                 """,
            command => command.Parameters.AddWithValue("$conversation_id", conversationId)).ConfigureAwait(false);

        await probe.ExecuteAsync("""
                                 INSERT INTO messages (message_id, conversation_id, sequence, role, content, created_at_utc)
                                 VALUES ($message_id, $conversation_id, 0, 'assistant', X'00', 1234);
                                 """,
            command =>
            {
                command.Parameters.AddWithValue("$message_id", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$conversation_id", conversationId);
            }).ConfigureAwait(false);

        await probe.ExecuteAsync("""
                                 INSERT INTO tool_events (tool_call_id, conversation_id, tool_name, status, created_at_utc)
                                 VALUES ($tool_call_id, $conversation_id, 'list_files', 'completed', 1234);
                                 """,
            command =>
            {
                command.Parameters.AddWithValue("$tool_call_id", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$conversation_id", conversationId);
            }).ConfigureAwait(false);
    }
}
