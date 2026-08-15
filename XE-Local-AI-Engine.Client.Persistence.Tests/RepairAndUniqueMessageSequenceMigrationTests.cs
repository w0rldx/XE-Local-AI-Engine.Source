namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>RepairAndUniqueMessageSequence</c> closes the pre-lock send race — two concurrent sends both reading
///     <c>MAX(sequence) + 1</c> — by making <c>(conversation_id, sequence)</c> unique. It cannot just create the index:
///     databases in the field already carry the collisions that race produced, and <c>CREATE UNIQUE INDEX</c> would
///     fail on them, leaving the node unable to start. So the migration first renumbers every conversation to a
///     contiguous 0-based order, preserving the existing sequence order and breaking ties by created-at then id, which
///     leaves well-formed data untouched and separates only genuine collisions. Both halves are asserted here: the
///     repair over colliding rows, and the constraint that stops the collision recurring.
/// </summary>
public sealed class RepairAndUniqueMessageSequenceMigrationTests
{
    private const string PreRepairMigrationId = "20260711002326_AddBenchmarkProfileRevisionBinding";
    private const string ThisMigrationId = "20260713170221_RepairAndUniqueMessageSequence";

    [Test]
    public async Task Migrate_OverCollidingSequences_RenumbersEachConversationContiguously()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("repair-sequence.sqlite", PreRepairMigrationId).ConfigureAwait(false);

        var conversationId = Guid.NewGuid().ToString();
        await InsertConversationAsync(probe, conversationId).ConfigureAwait(false);

        // Two messages collide on sequence 5; a third sorts ahead of both. The repair orders by (sequence, created_at,
        // message_id), so the expected result is early → 0, then the two colliding rows in created-at order → 1, 2.
        await InsertMessageAsync(probe, conversationId, sequence: 5, createdAtUtc: 100).ConfigureAwait(false);
        await InsertMessageAsync(probe, conversationId, sequence: 5, createdAtUtc: 200).ConfigureAwait(false);
        await InsertMessageAsync(probe, conversationId, sequence: 3, createdAtUtc: 50).ConfigureAwait(false);

        await probe.MigrateToAsync(ThisMigrationId).ConfigureAwait(false);

        var sequences = await probe.LongsAsync("SELECT sequence FROM messages ORDER BY created_at_utc;").ConfigureAwait(false);

        AssertEx.True(sequences.SequenceEqual(new[] { 0L, 1L, 2L }),
            $"Colliding sequences must be renumbered contiguously in the original order; got [{string.Join(", ", sequences)}].");
    }

    [Test]
    public async Task Migrate_OverWellFormedSequences_LeavesThemUntouched()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("repair-sequence-noop.sqlite", PreRepairMigrationId).ConfigureAwait(false);

        var conversationId = Guid.NewGuid().ToString();
        await InsertConversationAsync(probe, conversationId).ConfigureAwait(false);
        await InsertMessageAsync(probe, conversationId, sequence: 0, createdAtUtc: 100).ConfigureAwait(false);
        await InsertMessageAsync(probe, conversationId, sequence: 1, createdAtUtc: 200).ConfigureAwait(false);
        await InsertMessageAsync(probe, conversationId, sequence: 2, createdAtUtc: 300).ConfigureAwait(false);

        await probe.MigrateToAsync(ThisMigrationId).ConfigureAwait(false);

        var sequences = await probe.LongsAsync("SELECT sequence FROM messages ORDER BY created_at_utc;").ConfigureAwait(false);

        AssertEx.True(sequences.SequenceEqual(new[] { 0L, 1L, 2L }),
            $"An already-contiguous conversation must come through unchanged; got [{string.Join(", ", sequences)}].");
    }

    [Test]
    public async Task Migrate_ToThisMigration_ReplacesTheConversationIndexWithAUniqueSequenceIndex()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("repair-sequence-index.sqlite", ThisMigrationId).ConfigureAwait(false);

        AssertEx.True(await probe.IndexExistsAsync("messages",
                "IX_messages_conversation_id_sequence",
                unique: true,
                "conversation_id",
                "sequence").ConfigureAwait(false),
            "The sequence must be uniquely indexed per conversation.");

        AssertEx.False(await probe.IndexExistsAsync("messages", "IX_messages_conversation_id", unique: false, "conversation_id").ConfigureAwait(false),
            "The superseded non-unique index must be dropped, not left alongside the new one.");

        var conversationId = Guid.NewGuid().ToString();
        await InsertConversationAsync(probe, conversationId).ConfigureAwait(false);
        await InsertMessageAsync(probe, conversationId, sequence: 0, createdAtUtc: 100).ConfigureAwait(false);

        await AssertEx.ThrowsAsync<SqliteException>(() => InsertMessageAsync(probe, conversationId, sequence: 0, createdAtUtc: 200),
            "The race this migration exists for must now be rejected by the database.").ConfigureAwait(false);
    }

    private static Task InsertConversationAsync(MigrationSchemaProbe probe, string conversationId)
    {
        return probe.ExecuteAsync("""
                                  INSERT INTO conversations (conversation_id, title, user_id, created_at_utc, last_seen_utc, purged)
                                  VALUES ($conversation_id, 'Historical', 'node', 1234, 1234, 0);
                                  """,
            command => command.Parameters.AddWithValue("$conversation_id", conversationId));
    }

    private static Task InsertMessageAsync(MigrationSchemaProbe probe, string conversationId, int sequence, long createdAtUtc)
    {
        return probe.ExecuteAsync("""
                                  INSERT INTO messages (message_id, conversation_id, sequence, role, content, created_at_utc, updated_at_utc, status)
                                  VALUES ($message_id, $conversation_id, $sequence, 'assistant', X'00', $created_at_utc, $created_at_utc, 'Completed');
                                  """,
            command =>
            {
                command.Parameters.AddWithValue("$message_id", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$conversation_id", conversationId);
                command.Parameters.AddWithValue("$sequence", sequence);
                command.Parameters.AddWithValue("$created_at_utc", createdAtUtc);
            });
    }
}
