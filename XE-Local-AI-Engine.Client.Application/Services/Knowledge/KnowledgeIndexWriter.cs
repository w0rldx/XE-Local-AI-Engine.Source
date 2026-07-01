namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Default <see cref="IKnowledgeIndexWriter" />. Writes sections, chunks, and vectors for a document over the raw-SQL
///     path in a single transaction so the FTS insert triggers fire and the write is atomic. Scoped: it depends on the
///     scoped <see cref="NodeChatDbContext" /> and runs inside the per-ingestion-job scope. The write is idempotent — it
///     first purges any existing rows for the document (ordered vectors → chunks → sections) so a retry does not duplicate.
/// </summary>
public sealed class KnowledgeIndexWriter : IKnowledgeIndexWriter
{
    private readonly NodeChatDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public KnowledgeIndexWriter(NodeChatDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<bool> WriteAsync(KnowledgeIndexInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Delete-vs-ingestion race guard: if the document row was removed while this job was embedding, write nothing.
        if (!await DocumentExistsAsync(connection, transaction, input.DocumentId, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await PurgeDocumentRowsAsync(connection, transaction, input.DocumentId, cancellationToken).ConfigureAwait(false);
        var sectionIdsByOrdinal = await InsertSectionsAsync(connection, transaction, input, cancellationToken).ConfigureAwait(false);
        await InsertChunksAndVectorsAsync(connection, transaction, input, sectionIdsByOrdinal, cancellationToken).ConfigureAwait(false);
        await MarkIndexedAsync(connection, transaction, input, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    // Idempotent rewrite: purge existing rows for the document in FK-safe order (vectors → chunks → sections). The chunk
    // delete fires the FTS delete trigger, keeping the external-content index aligned.
    private static async Task PurgeDocumentRowsAsync(DbConnection connection, DbTransaction transaction, Guid documentId, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, "DELETE FROM knowledge_chunk_vectors WHERE document_id = $document_id;",
            cancellationToken, ("$document_id", documentId)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM knowledge_document_chunks WHERE document_id = $document_id;",
            cancellationToken, ("$document_id", documentId)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM knowledge_document_sections WHERE document_id = $document_id;",
            cancellationToken, ("$document_id", documentId)).ConfigureAwait(false);
    }

    private static async Task<Dictionary<int, Guid>> InsertSectionsAsync(DbConnection connection,
        DbTransaction transaction,
        KnowledgeIndexInput input,
        CancellationToken cancellationToken)
    {
        var sectionIdsByOrdinal = new Dictionary<int, Guid>(input.Sections.Count);
        foreach (var section in input.Sections)
        {
            var sectionId = Guid.NewGuid();
            sectionIdsByOrdinal[section.Ordinal] = sectionId;

            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO knowledge_document_sections (section_id, document_id, ordinal, heading, level)
                VALUES ($section_id, $document_id, $ordinal, $heading, $level);
                """,
                cancellationToken,
                ("$section_id", sectionId),
                ("$document_id", input.DocumentId),
                ("$ordinal", section.Ordinal),
                ("$heading", section.Heading),
                ("$level", section.Level)).ConfigureAwait(false);
        }

        return sectionIdsByOrdinal;
    }

    private static async Task InsertChunksAndVectorsAsync(DbConnection connection,
        DbTransaction transaction,
        KnowledgeIndexInput input,
        IReadOnlyDictionary<int, Guid> sectionIdsByOrdinal,
        CancellationToken cancellationToken)
    {
        foreach (var chunk in input.Chunks)
        {
            var chunkId = Guid.NewGuid();
            var sectionId = sectionIdsByOrdinal.TryGetValue(chunk.SectionOrdinal, out var owningSection)
                ? (Guid?)owningSection
                : null;

            // rowid is an auto-assigned INTEGER PRIMARY KEY alias — omit it so SQLite generates it and the AFTER INSERT
            // FTS trigger mirrors the row into chunk_fts.
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO knowledge_document_chunks (chunk_id, document_id, section_id, chunk_index, content, token_count, heading_path)
                VALUES ($chunk_id, $document_id, $section_id, $chunk_index, $content, $token_count, $heading_path);
                """,
                cancellationToken,
                ("$chunk_id", chunkId),
                ("$document_id", input.DocumentId),
                ("$section_id", sectionId),
                ("$chunk_index", chunk.ChunkIndex),
                ("$content", chunk.Content),
                ("$token_count", chunk.TokenCount),
                ("$heading_path", chunk.HeadingPath)).ConfigureAwait(false);

            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO knowledge_chunk_vectors (chunk_id, document_id, dim, embedding, embedding_model)
                VALUES ($chunk_id, $document_id, $dim, $embedding, $embedding_model);
                """,
                cancellationToken,
                ("$chunk_id", chunkId),
                ("$document_id", input.DocumentId),
                ("$dim", chunk.Dim),
                ("$embedding", chunk.Embedding),
                ("$embedding_model", input.EmbeddingModel)).ConfigureAwait(false);
        }
    }

    private async Task MarkIndexedAsync(DbConnection connection, DbTransaction transaction, KnowledgeIndexInput input, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await ExecuteAsync(connection, transaction,
            """
            UPDATE knowledge_documents
            SET status = $status, failure_reason = NULL, chunk_count = $chunk_count, embedding_model = $embedding_model, updated_at_utc = $updated_at_utc
            WHERE document_id = $document_id;
            """,
            cancellationToken,
            ("$status", KnowledgeDocumentStatus.Indexed.ToString()),
            ("$chunk_count", input.Chunks.Count),
            ("$embedding_model", input.EmbeddingModel),
            ("$updated_at_utc", now),
            ("$document_id", input.DocumentId)).ConfigureAwait(false);
    }

    private static async Task<bool> DocumentExistsAsync(DbConnection connection, DbTransaction transaction, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM knowledge_documents WHERE document_id = $document_id;";
        AddParameter(command, "$document_id", documentId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null and not DBNull;
    }

    private static async Task ExecuteAsync(DbConnection connection,
        DbTransaction transaction,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            AddParameter(command, name, value);
        }

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
