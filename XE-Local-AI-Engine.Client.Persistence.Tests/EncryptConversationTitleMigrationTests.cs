namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>EncryptConversationTitle</c> is a data migration, not a schema one: it <b>destroys</b> every existing
///     plaintext title before widening the column to a BLOB, because a migration has no access to the node key and so
///     cannot encrypt what it finds. Losing the titles is the deliberate trade — leaving them would leave user content
///     readable at rest in a column the product now treats as ciphertext — and
///     <c>NodeChatTitleEncryptionBackfillService</c> re-derives them from each conversation's first user message at the
///     next startup. The conversation itself must survive; only its title is cleared.
/// </summary>
public sealed class EncryptConversationTitleMigrationTests
{
    private const string PreEncryptionMigrationId = "20260608093959_AddMessageAgentDefinitionId";
    private const string ThisMigrationId = "20260610165152_EncryptConversationTitle";

    [Test]
    public async Task Migrate_OverAPlaintextTitle_ClearsItAndKeepsTheConversation()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("encrypt-title.sqlite", PreEncryptionMigrationId).ConfigureAwait(false);

        var conversationId = Guid.NewGuid().ToString();
        await probe.ExecuteAsync("""
                                 INSERT INTO conversations (conversation_id, title, user_id, created_at_utc, last_seen_utc, purged)
                                 VALUES ($conversation_id, 'Quarterly revenue plan', 'node', 1234, 1234, 0);
                                 """,
            command => command.Parameters.AddWithValue("$conversation_id", conversationId)).ConfigureAwait(false);

        await probe.MigrateToAsync(ThisMigrationId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1L, (await probe.LongsAsync("SELECT COUNT(*) FROM conversations;").ConfigureAwait(false)).Single(),
            "The conversation must survive; only its title is cleared.");

        var title = await probe.ScalarAsync("SELECT title FROM conversations WHERE conversation_id = $id;",
            command => command.Parameters.AddWithValue("$id", conversationId)).ConfigureAwait(false);
        AssertEx.Null(title, "The pre-migration plaintext title must not survive the migration.");

        AssertEx.Equal(expected: 0L,
            (await probe.LongsAsync("SELECT COUNT(*) FROM conversations WHERE title IS NOT NULL;").ConfigureAwait(false)).Single(),
            "No conversation may keep a title through this migration.");
    }

    [Test]
    public async Task Migrate_ToThisMigration_WidensTheTitleColumnToABlob()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("encrypt-title-column.sqlite", ThisMigrationId).ConfigureAwait(false);

        var declaredType = await probe.ScalarAsync("SELECT type FROM pragma_table_info('conversations') WHERE name = 'title';").ConfigureAwait(false);

        AssertEx.Equal("BLOB", AssertEx.NotNull(declaredType as string, "conversations.title must exist."),
            "The title column must be a BLOB — the product writes AEAD ciphertext into it, not text.");
    }
}
