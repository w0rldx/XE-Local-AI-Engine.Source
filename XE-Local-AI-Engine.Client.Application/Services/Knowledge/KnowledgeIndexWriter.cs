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
        await using (var vectorsCommand = connection.CreateCommand())
        {
            vectorsCommand.Transaction = transaction;
            vectorsCommand.CommandText = "DELETE FROM knowledge_chunk_vectors WHERE document_id = $document_id;";
            AddParameter(vectorsCommand, "$document_id", documentId);
            _ = await vectorsCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var chunksCommand = connection.CreateCommand())
        {
            chunksCommand.Transaction = transaction;
            chunksCommand.CommandText = "DELETE FROM knowledge_document_chunks WHERE document_id = $document_id;";
            AddParameter(chunksCommand, "$document_id", documentId);
            _ = await chunksCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var sectionsCommand = connection.CreateCommand())
        {
            sectionsCommand.Transaction = transaction;
            sectionsCommand.CommandText = "DELETE FROM knowledge_document_sections WHERE document_id = $document_id;";
            AddParameter(sectionsCommand, "$document_id", documentId);
            _ = await sectionsCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
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

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                                   INSERT INTO knowledge_document_sections (section_id, document_id, ordinal, heading, level)
                                   VALUES ($section_id, $document_id, $ordinal, $heading, $level);
                                   """;
            AddParameter(command, "$section_id", sectionId);
            AddParameter(command, "$document_id", input.DocumentId);
            AddParameter(command, "$ordinal", section.Ordinal);
            AddParameter(command, "$heading", section.Heading);
            AddParameter(command, "$level", section.Level);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
            await using (var chunkCommand = connection.CreateCommand())
            {
                chunkCommand.Transaction = transaction;
                chunkCommand.CommandText = """
                                            INSERT INTO knowledge_document_chunks (chunk_id, document_id, section_id, chunk_index, content, token_count, heading_path)
                                            VALUES ($chunk_id, $document_id, $section_id, $chunk_index, $content, $token_count, $heading_path);
                                            """;
                AddParameter(chunkCommand, "$chunk_id", chunkId);
                AddParameter(chunkCommand, "$document_id", input.DocumentId);
                AddParameter(chunkCommand, "$section_id", sectionId);
                AddParameter(chunkCommand, "$chunk_index", chunk.ChunkIndex);
                AddParameter(chunkCommand, "$content", chunk.Content);
                AddParameter(chunkCommand, "$token_count", chunk.TokenCount);
                AddParameter(chunkCommand, "$heading_path", chunk.HeadingPath);
                _ = await chunkCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var vectorCommand = connection.CreateCommand())
            {
                vectorCommand.Transaction = transaction;
                vectorCommand.CommandText = """
                                             INSERT INTO knowledge_chunk_vectors (chunk_id, document_id, dim, embedding, embedding_model)
                                             VALUES ($chunk_id, $document_id, $dim, $embedding, $embedding_model);
                                             """;
                AddParameter(vectorCommand, "$chunk_id", chunkId);
                AddParameter(vectorCommand, "$document_id", input.DocumentId);
                AddParameter(vectorCommand, "$dim", chunk.Dim);
                AddParameter(vectorCommand, "$embedding", chunk.Embedding.ToArray());
                AddParameter(vectorCommand, "$embedding_model", input.EmbeddingModel);
                _ = await vectorCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task MarkIndexedAsync(DbConnection connection, DbTransaction transaction, KnowledgeIndexInput input, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                               UPDATE knowledge_documents
                               SET status = $status, failure_reason = NULL, chunk_count = $chunk_count, embedding_model = $embedding_model, updated_at_utc = $updated_at_utc
                               WHERE document_id = $document_id;
                               """;
        AddParameter(command, "$status", KnowledgeDocumentStatus.Indexed.ToString());
        AddParameter(command, "$chunk_count", input.Chunks.Count);
        AddParameter(command, "$embedding_model", input.EmbeddingModel);
        AddParameter(command, "$updated_at_utc", now);
        AddParameter(command, "$document_id", input.DocumentId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
}
