namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Pins the <c>AddKnowledgeIngestionAttempts</c> migration: existing documents start at zero, an insert that does not
///     name the column (the raw-SQL upload path) gets the default, and a one-step rollback keeps every document.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddKnowledgeIngestionAttemptsMigrationTests
{
    private const string PreviousMigrationId = "20261002082214_AddImageEditColumns";
    private const string ThisMigrationId = "20261008091329_AddKnowledgeIngestionAttempts";
    private const string Column = "ingestion_attempts";

    [Test]
    public async Task MigrateAsync_WhenUpgradedWithAnExistingDocument_AddsTheColumnAtZero()
    {
        await using var probe = await SeededProbeAsync("knowledge-attempts-up.sqlite");

        await probe.MigrateToAsync(ThisMigrationId);

        AssertEx.True((await probe.ColumnsAsync("knowledge_documents")).Contains(Column), "The migration must add ingestion_attempts.");
        AssertEx.Equal("0", await probe.ColumnDefaultAsync("knowledge_documents", Column), "The column needs a database default of 0.");
        AssertEx.Equal(1L, (await probe.LongsAsync("SELECT \"notnull\" FROM pragma_table_info('knowledge_documents') WHERE name = 'ingestion_attempts';")).Single(),
            "The column is NOT NULL.");
        AssertEx.Equal(0L, (await probe.LongsAsync("SELECT ingestion_attempts FROM knowledge_documents;")).Single(), "An existing document starts at zero.");

        await InsertDocumentAsync(probe, "B");
        AssertEx.Equal(0L, (await probe.LongsAsync("SELECT ingestion_attempts FROM knowledge_documents WHERE content_hash = 'hash-B';")).Single(),
            "An insert that does not name the column must get the default.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBackOneStep_DropsTheColumnAndKeepsTheDocuments()
    {
        await using var probe = await SeededProbeAsync("knowledge-attempts-rollback.sqlite");
        await probe.MigrateToAsync(ThisMigrationId);

        await probe.MigrateToAsync(PreviousMigrationId);

        var columns = await probe.ColumnsAsync("knowledge_documents");
        AssertEx.False(columns.Contains(Column), "Rolling back one step must drop ingestion_attempts.");
        AssertEx.True(columns.Contains("failure_reason") && columns.Contains("parser_version"), "The rollback must keep the columns beside it.");
        AssertEx.Equal(1L, (await probe.LongsAsync("SELECT COUNT(*) FROM knowledge_documents;")).Single(), "The rollback must keep every document.");
    }

    private static async Task<MigrationSchemaProbe> SeededProbeAsync(string fileName)
    {
        var probe = await MigrationSchemaProbe.FromChatTemplateAsync(fileName, PreviousMigrationId);
        try
        {
            AssertEx.False((await probe.ColumnsAsync("knowledge_documents")).Contains(Column), "The predecessor must not have the column yet.");
            await InsertDocumentAsync(probe, "A");
            return probe;
        }
        catch
        {
            await probe.DisposeAsync();
            throw;
        }
    }

    private static async Task InsertDocumentAsync(MigrationSchemaProbe probe, string suffix)
    {
        await probe.ExecuteAsync("""
                                 INSERT INTO knowledge_documents (document_id, original_file_name, mime_type, extension, size_bytes, content_hash, storage_path, status, chunk_count, embedding_model, created_at_utc, updated_at_utc)
                                 VALUES ($id, X'01', 'text/plain', '.txt', 10, $hash, $path, 'Pending', 0, 'nomic-embed-text', 1, 1);
                                 """,
            command =>
            {
                var id = Guid.NewGuid().ToString("D").ToUpperInvariant();
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$hash", "hash-" + suffix);
                command.Parameters.AddWithValue("$path", id + ".txt");
            });
    }
}
