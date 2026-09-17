namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddKnowledgeCollectionsAndProvenance</c> re-scopes the knowledge base from one flat corpus to named
///     collections and records how each document was parsed and chunked. Two consequences are asserted here because
///     nothing else pins them. First, dedupe moves scope: the unique index on <c>content_hash</c> alone is replaced by
///     one on <c>(collection_id, content_hash)</c>, so the same file may exist once per collection but still only once
///     within one — without the move, adding a document to a second collection would be rejected as a duplicate.
///     Second, existing documents predate every provenance field, so they backfill to the honest <c>legacy</c>
///     placeholder rather than to the current parser/chunker version, which would claim they had been produced by code
///     that never touched them and defeat the re-index check that reads these columns.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddKnowledgeCollectionsAndProvenanceMigrationTests
{
    private const string PreCollectionsMigrationId = "20260811161453_AddModelLaunchArguments";
    private const string ThisMigrationId = "20260813121930_AddKnowledgeCollectionsAndProvenance";

    [Test]
    public async Task Migrate_OverALegacyDocument_BackfillsTheDefaultCollectionAndLegacyProvenance()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("knowledge-collections.sqlite", PreCollectionsMigrationId);

        await InsertLegacyDocumentAsync(probe, Guid.NewGuid().ToString(), "hash-a");

        await probe.MigrateToAsync(ThisMigrationId);

        // The database carries exactly the one seeded document, so each read needs no predicate.
        AssertEx.Equal("DEFAULT", await TextAsync(probe, "SELECT collection_id FROM knowledge_documents;"),
            "A document that predates collections belongs to the default one.");
        AssertEx.Equal("upload", await TextAsync(probe, "SELECT source_kind FROM knowledge_documents;"),
            "Everything ingested before repository sources existed arrived as an upload.");
        AssertEx.Equal("legacy", await TextAsync(probe, "SELECT parser_version FROM knowledge_documents;"),
            "A historical document must not claim to have been produced by the current parser.");
        AssertEx.Equal("legacy", await TextAsync(probe, "SELECT chunker_version FROM knowledge_documents;"),
            "A historical document must not claim to have been produced by the current chunker.");
    }

    [Test]
    public async Task Migrate_ToThisMigration_ScopesDedupeToTheCollection()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("knowledge-collections-dedupe.sqlite", ThisMigrationId);

        AssertEx.False(await probe.IndexExistsAsync("knowledge_documents", "IX_knowledge_documents_content_hash", unique: true, "content_hash"),
            "The corpus-wide content-hash index must be gone, or a file could never be added to a second collection.");
        AssertEx.True(await probe.IndexExistsAsync("knowledge_documents",
                "IX_knowledge_documents_collection_id_content_hash",
                unique: true,
                "collection_id",
                "content_hash"),
            "Dedupe must be uniquely indexed per collection.");

        await InsertDocumentAsync(probe, Guid.NewGuid().ToString(), "hash-a", collectionId: "DEFAULT");
        await InsertDocumentAsync(probe, Guid.NewGuid().ToString(), "hash-a", collectionId: "project-a");

        await AssertEx.ThrowsAsync<SqliteException>(() => InsertDocumentAsync(probe, Guid.NewGuid().ToString(), "hash-a", collectionId: "project-a"),
            "The same file must still be rejected as a duplicate within one collection.");
    }

    [Test]
    public async Task Migrate_ToThisMigration_RebuildsTheChunkIndexOverTheProvenanceColumns()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("knowledge-collections-fts.sqlite", ThisMigrationId);

        AssertEx.True(await probe.TableExistsAsync("chunk_fts"), "The full-text index must exist.");

        // The rebuild is what makes a path or symbol query reach the index at all; a chunk_fts left at its old shape
        // would still answer content queries, so the missing columns are the only thing that would show the miss.
        AssertEx.True((await probe.ColumnsAsync("chunk_fts")).IsSupersetOf(new[]
        {
            "chunk_id",
            "document_id",
            "source_path",
            "heading_path",
            "symbol",
            "content"
        }), "chunk_fts must index the provenance columns this migration added.");

        AssertEx.True((await probe.ColumnsAsync("knowledge_document_chunks")).IsSupersetOf(new[]
        {
            "content_hash",
            "content_kind",
            "embedding_input_hash",
            "start_offset",
            "end_offset",
            "language",
            "page_number",
            "source_path",
            "symbol"
        }), "Chunks must carry the provenance and location columns retrieval reports back.");
    }

    /// <summary>A document as it could be written before this migration existed — no collection, no provenance.</summary>
    private static Task InsertLegacyDocumentAsync(MigrationSchemaProbe probe, string documentId, string contentHash)
    {
        return probe.ExecuteAsync("""
                                  INSERT INTO knowledge_documents
                                      (document_id, original_file_name, mime_type, extension, size_bytes, content_hash,
                                       storage_path, status, chunk_count, embedding_model, created_at_utc, updated_at_utc)
                                  VALUES ($document_id, X'00', 'text/plain', '.txt', 10, $content_hash,
                                          $storage_path, 'Indexed', 1, 'qwen3-embedding:0.6b', 1234, 1234);
                                  """,
            command => Bind(command, documentId, contentHash));
    }

    private static Task InsertDocumentAsync(MigrationSchemaProbe probe, string documentId, string contentHash, string collectionId)
    {
        return probe.ExecuteAsync("""
                                  INSERT INTO knowledge_documents
                                      (document_id, original_file_name, mime_type, extension, size_bytes, content_hash,
                                       storage_path, status, chunk_count, embedding_model, created_at_utc, updated_at_utc, collection_id)
                                  VALUES ($document_id, X'00', 'text/plain', '.txt', 10, $content_hash,
                                          $storage_path, 'Indexed', 1, 'qwen3-embedding:0.6b', 1234, 1234, $collection_id);
                                  """,
            command =>
            {
                Bind(command, documentId, contentHash);
                command.Parameters.AddWithValue("$collection_id", collectionId);
            });
    }

    private static void Bind(SqliteCommand command, string documentId, string contentHash)
    {
        command.Parameters.AddWithValue("$document_id", documentId);
        command.Parameters.AddWithValue("$content_hash", contentHash);
        command.Parameters.AddWithValue("$storage_path", documentId + ".bin");
    }

    private static async Task<string> TextAsync(MigrationSchemaProbe probe, string sql)
    {
        return AssertEx.NotNull(await probe.ScalarAsync(sql) as string,
            "The column must be non-null after the backfill.");
    }
}
