namespace XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

using System.Data.Common;
using Microsoft.Data.Sqlite;

/// <summary>
///     Seeds the document and chunk a <c>knowledge_chunk_vectors</c> row declares. The node enforces foreign keys, so a
///     bare vector row is a fixture production cannot produce — and, since Wave 5, one SQLite refuses to insert.
/// </summary>
internal static class KnowledgeVectorParents
{
    public static async Task EnsureAsync(SqliteConnection connection, Guid chunkId, Guid documentId, DbTransaction? transaction = null)
    {
        await using (var document = connection.CreateCommand())
        {
            document.Transaction = (SqliteTransaction?)transaction;
            document.CommandText =
                """
                INSERT OR IGNORE INTO knowledge_documents (document_id, original_file_name, mime_type, extension, size_bytes, content_hash, storage_path, status, chunk_count, embedding_model, created_at_utc, updated_at_utc)
                VALUES ($did, $name, 'text/plain', '.txt', 10, $hash, $path, 'Indexed', 1, 'nomic-embed-text', 1, 1);
                """;
            _ = document.Parameters.AddWithValue("$did", documentId);
            _ = document.Parameters.AddWithValue("$name", new byte[]
            {
                1,
                2,
                3
            });
            _ = document.Parameters.AddWithValue("$hash", "hash-" + documentId.ToString("N"));
            _ = document.Parameters.AddWithValue("$path", documentId.ToString("D") + ".txt");
            _ = await document.ExecuteNonQueryAsync();
        }

        await using var chunk = connection.CreateCommand();
        chunk.Transaction = (SqliteTransaction?)transaction;
        chunk.CommandText =
            """
            INSERT OR IGNORE INTO knowledge_document_chunks (chunk_id, document_id, chunk_index, content, token_count)
            VALUES ($cid, $did, (SELECT COUNT(*) FROM knowledge_document_chunks WHERE document_id = $did), 'vector fixture', 1);
            """;
        _ = chunk.Parameters.AddWithValue("$cid", chunkId);
        _ = chunk.Parameters.AddWithValue("$did", documentId);
        _ = await chunk.ExecuteNonQueryAsync();
    }
}
