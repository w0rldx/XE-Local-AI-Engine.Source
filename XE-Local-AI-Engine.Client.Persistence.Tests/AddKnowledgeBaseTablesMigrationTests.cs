namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddKnowledgeBaseTables</c> creates the RAG store: documents, their sections, the chunks retrieval scores, and
///     the chunk vectors. Every child cascades off <c>knowledge_documents</c>, which is what makes deleting a document
///     actually delete its embeddings instead of stranding them.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddKnowledgeBaseTablesMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_CreatesTheDocumentSectionChunkVectorChain()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("knowledge-base-tables.sqlite");

        foreach (var table in new[]
                 {
                     "knowledge_documents",
                     "knowledge_document_sections",
                     "knowledge_document_chunks",
                     "knowledge_chunk_vectors"
                 })
        {
            AssertEx.True(await probe.TableExistsAsync(table), $"{table} must exist.");
        }

        AssertEx.True((await probe.ColumnsAsync("knowledge_documents")).IsSupersetOf(new[]
        {
            "document_id",
            "original_file_name",
            "mime_type",
            "content_hash",
            "storage_path",
            "status",
            "chunk_count",
            "embedding_model",
            "created_at_utc",
            "updated_at_utc"
        }), "knowledge_documents must expose the mapped columns.");

        AssertEx.True((await probe.ColumnsAsync("knowledge_chunk_vectors")).IsSupersetOf(new[]
        {
            "chunk_id",
            "document_id",
            "dim",
            "embedding",
            "embedding_model"
        }), "knowledge_chunk_vectors must expose the mapped columns.");

        // Deleting a document has to take its whole subtree with it; each hop is a declared FK back to the document.
        AssertEx.True(await probe.ForeignKeyExistsAsync("knowledge_document_sections", "document_id", "knowledge_documents"),
            "Sections must be foreign-keyed to their document.");
        AssertEx.True(await probe.ForeignKeyExistsAsync("knowledge_document_chunks", "document_id", "knowledge_documents"),
            "Chunks must be foreign-keyed to their document.");
        AssertEx.True(await probe.ForeignKeyExistsAsync("knowledge_document_chunks", "section_id", "knowledge_document_sections"),
            "Chunks must be foreign-keyed to their section.");
        AssertEx.True(await probe.ForeignKeyExistsAsync("knowledge_chunk_vectors", "chunk_id", "knowledge_document_chunks"),
            "Vectors must be foreign-keyed to their chunk.");

        AssertEx.True(await probe.IndexExistsAsync("knowledge_document_chunks",
                "IX_knowledge_document_chunks_document_id_chunk_index",
                unique: false,
                "document_id",
                "chunk_index"),
            "Chunks must be indexed in document order.");

        AssertEx.True(await probe.IndexExistsAsync("knowledge_document_sections",
                "IX_knowledge_document_sections_document_id_ordinal",
                unique: false,
                "document_id",
                "ordinal"),
            "Sections must be indexed in document order.");

        AssertEx.True(await probe.IndexExistsAsync("knowledge_chunk_vectors",
                "IX_knowledge_chunk_vectors_embedding_model",
                unique: false,
                "embedding_model"),
            "The embedding-model scan (used by the downgrade-safety check) must be indexed.");
    }
}
