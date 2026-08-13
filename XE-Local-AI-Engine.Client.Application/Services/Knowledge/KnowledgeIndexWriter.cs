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

        // Repository updates preserve document_id, so existence alone cannot prove that the embedded chunks came from the
        // current blob. Commit only against the exact content-hash revision captured before extraction.
        var currentContentHash = await ReadCurrentContentHashAsync(connection,
                transaction,
                input.DocumentId,
                cancellationToken)
            .ConfigureAwait(false);
        if (currentContentHash is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!string.Equals(currentContentHash, input.SourceContentHash, StringComparison.Ordinal))
        {
            await PreserveCurrentRevisionPendingAsync(connection, transaction, input.DocumentId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await PurgeDocumentRowsAsync(connection, transaction, input.DocumentId, cancellationToken).ConfigureAwait(false);
        var sectionIdsByOrdinal = await InsertSectionsAsync(connection, transaction, input, cancellationToken).ConfigureAwait(false);
        await InsertChunksAndVectorsAsync(connection, transaction, input, sectionIdsByOrdinal, cancellationToken).ConfigureAwait(false);
        if (!await MarkIndexedAsync(connection, transaction, input, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

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
                                  INSERT INTO knowledge_document_sections (section_id, document_id, ordinal, heading, level, page_number)
                                  VALUES ($section_id, $document_id, $ordinal, $heading, $level, $page_number);
                                  """;
            AddParameter(command, "$section_id", sectionId);
            AddParameter(command, "$document_id", input.DocumentId);
            AddParameter(command, "$ordinal", section.Ordinal);
            AddParameter(command, "$heading", section.Heading);
            AddParameter(command, "$level", section.Level);
            AddParameter(command, "$page_number", section.PageNumber);
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
                                           INSERT INTO knowledge_document_chunks
                                               (chunk_id, document_id, section_id, chunk_index, content, token_count, heading_path,
                                                page_number, start_offset, end_offset, content_kind, source_path, language, symbol,
                                                content_hash, embedding_input_hash)
                                           VALUES
                                               ($chunk_id, $document_id, $section_id, $chunk_index, $content, $token_count, $heading_path,
                                                $page_number, $start_offset, $end_offset, $content_kind, $source_path, $language, $symbol,
                                                $content_hash, $embedding_input_hash);
                                           """;
                AddParameter(chunkCommand, "$chunk_id", chunkId);
                AddParameter(chunkCommand, "$document_id", input.DocumentId);
                AddParameter(chunkCommand, "$section_id", sectionId);
                AddParameter(chunkCommand, "$chunk_index", chunk.ChunkIndex);
                AddParameter(chunkCommand, "$content", chunk.Content);
                AddParameter(chunkCommand, "$token_count", chunk.TokenCount);
                AddParameter(chunkCommand, "$heading_path", chunk.HeadingPath);
                AddParameter(chunkCommand, "$page_number", chunk.PageNumber);
                AddParameter(chunkCommand, "$start_offset", chunk.StartOffset);
                AddParameter(chunkCommand, "$end_offset", chunk.EndOffset);
                AddParameter(chunkCommand, "$content_kind", chunk.ContentKind);
                AddParameter(chunkCommand, "$source_path", chunk.SourcePath);
                AddParameter(chunkCommand, "$language", chunk.Language);
                AddParameter(chunkCommand, "$symbol", chunk.Symbol);
                AddParameter(chunkCommand, "$content_hash", chunk.ContentHash);
                AddParameter(chunkCommand, "$embedding_input_hash", chunk.EmbeddingInputHash);
                _ = await chunkCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Store the vector L2-normalized. Cosine similarity is scale-invariant, so unit-length storage changes no
            // ranking, but it lets the search score with a plain dot product (query normalized once, one pass per
            // candidate) instead of recomputing both norms per row. A zero-magnitude embedding has no direction and is
            // left exactly as produced (the search skips it, matching the old cosine-NaN behavior).
            var embeddingBytes = chunk.Embedding.ToArray();
            KnowledgeVectorMath.NormalizeBytesInPlace(embeddingBytes);

            await using (var vectorCommand = connection.CreateCommand())
            {
                vectorCommand.Transaction = transaction;
                vectorCommand.CommandText = """
                                            INSERT INTO knowledge_chunk_vectors (chunk_id, document_id, dim, embedding, embedding_model, vector_identity)
                                            VALUES ($chunk_id, $document_id, $dim, $embedding, $embedding_model, $vector_identity);
                                            """;
                AddParameter(vectorCommand, "$chunk_id", chunkId);
                AddParameter(vectorCommand, "$document_id", input.DocumentId);
                AddParameter(vectorCommand, "$dim", chunk.Dim);
                AddParameter(vectorCommand, "$embedding", embeddingBytes);
                AddParameter(vectorCommand, "$embedding_model", input.EmbeddingModel);
                AddParameter(vectorCommand, "$vector_identity", input.VectorIdentity);
                _ = await vectorCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> MarkIndexedAsync(DbConnection connection,
        DbTransaction transaction,
        KnowledgeIndexInput input,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              UPDATE knowledge_documents
                              SET status = $status,
                                  failure_reason = NULL,
                                  chunk_count = $chunk_count,
                                  embedding_model = $embedding_model,
                                  vector_identity = $vector_identity,
                                  vector_dim = $vector_dim,
                                  parser_version = $parser_version,
                                  chunker_version = $chunker_version,
                                  updated_at_utc = $updated_at_utc
                              WHERE document_id = $document_id
                                AND content_hash = $content_hash;
                              """;
        AddParameter(command, "$status", KnowledgeDocumentStatus.Indexed.ToString());
        AddParameter(command, "$chunk_count", input.Chunks.Count);
        AddParameter(command, "$embedding_model", input.EmbeddingModel);
        AddParameter(command, "$vector_identity", input.VectorIdentity);
        AddParameter(command, "$vector_dim", input.VectorDimension);
        AddParameter(command, "$parser_version", input.ParserVersion);
        AddParameter(command, "$chunker_version", input.ChunkerVersion);
        AddParameter(command, "$updated_at_utc", now);
        AddParameter(command, "$document_id", input.DocumentId);
        AddParameter(command, "$content_hash", input.SourceContentHash);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task<string?> ReadCurrentContentHashAsync(DbConnection connection,
        DbTransaction transaction,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT content_hash FROM knowledge_documents WHERE document_id = $document_id;";
        AddParameter(command, "$document_id", documentId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : (string)result;
    }

    private async Task PreserveCurrentRevisionPendingAsync(DbConnection connection,
        DbTransaction transaction,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              UPDATE knowledge_documents
                              SET status = $status,
                                  failure_reason = NULL,
                                  chunk_count = 0,
                                  updated_at_utc = $updated_at_utc
                              WHERE document_id = $document_id;
                              """;
        AddParameter(command, "$status", KnowledgeDocumentStatus.Pending.ToString());
        AddParameter(command, "$updated_at_utc", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        AddParameter(command, "$document_id", documentId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
