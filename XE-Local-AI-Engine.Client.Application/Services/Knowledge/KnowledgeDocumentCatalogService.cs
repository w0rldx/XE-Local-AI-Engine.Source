namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Default <see cref="IKnowledgeDocumentCatalogService" />. Reads and lightly mutates the <c>knowledge_documents</c>
///     catalog over the raw-SQL path (matching the rest of the knowledge lane), decrypting the display name via the
///     matching <see cref="NodeChatDbContext" /> helper. The stale-model flag compares each row's stored embedding model
///     against <see cref="KnowledgeBaseOptions.EmbeddingModelName" />. Scoped: it uses the request-scoped db context.
/// </summary>
public sealed class KnowledgeDocumentCatalogService : IKnowledgeDocumentCatalogService
{
    private readonly NodeChatDbContext _dbContext;
    private readonly KnowledgeBaseOptions _options;
    private readonly TimeProvider _timeProvider;

    public KnowledgeDocumentCatalogService(NodeChatDbContext dbContext,
        IOptions<KnowledgeBaseOptions> options,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<IReadOnlyList<KnowledgeDocumentSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT document_id, original_file_name, status, failure_reason, chunk_count, embedding_model, size_bytes, created_at_utc
                              FROM knowledge_documents
                              ORDER BY created_at_utc DESC;
                              """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var documents = new List<KnowledgeDocumentSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var documentId = Guid.Parse(reader.GetString(0));
            var displayName = await DecryptNameAsync(reader, ordinal: 1, documentId, cancellationToken).ConfigureAwait(false);
            var embeddingModel = reader.GetString(5);
            documents.Add(new KnowledgeDocumentSummary(
                documentId,
                displayName,
                ParseStatus(reader.GetString(2)),
                await reader.IsDBNullAsync(ordinal: 3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
                reader.GetInt32(4),
                embeddingModel,
                IsStaleModel(embeddingModel),
                reader.GetInt64(6),
                reader.GetInt64(7)));
        }

        return documents;
    }

    public async Task<KnowledgeDocumentDetail?> GetAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        KnowledgeDocumentDetail? detail;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                                  SELECT document_id, original_file_name, status, failure_reason, chunk_count, embedding_model, size_bytes, created_at_utc, updated_at_utc
                                  FROM knowledge_documents
                                  WHERE document_id = $document_id;
                                  """;
            AddParameter(command, "$document_id", documentId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var displayName = await DecryptNameAsync(reader, ordinal: 1, documentId, cancellationToken).ConfigureAwait(false);
            var embeddingModel = reader.GetString(5);
            detail = new KnowledgeDocumentDetail(
                documentId,
                displayName,
                ParseStatus(reader.GetString(2)),
                await reader.IsDBNullAsync(ordinal: 3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
                reader.GetInt32(4),
                embeddingModel,
                IsStaleModel(embeddingModel),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                Chunks: []);
        }

        var chunks = await ReadChunksAsync(connection, documentId, cancellationToken).ConfigureAwait(false);
        return detail with { Chunks = chunks };
    }

    public async Task<KnowledgeDocumentStatus?> GetStatusAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM knowledge_documents WHERE document_id = $document_id;";
        AddParameter(command, "$document_id", documentId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string status ? ParseStatus(status) : null;
    }

    public async Task<bool> ResetToPendingAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              UPDATE knowledge_documents
                              SET status = $status, failure_reason = NULL, updated_at_utc = $updated_at_utc
                              WHERE document_id = $document_id;
                              """;
        AddParameter(command, "$status", KnowledgeDocumentStatus.Pending.ToString());
        AddParameter(command, "$updated_at_utc", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        AddParameter(command, "$document_id", documentId);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<IReadOnlyList<Guid>> ResetStaleDocumentsToPendingAsync(CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var staleIds = new List<Guid>();
        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = transaction;
            selectCommand.CommandText = "SELECT document_id FROM knowledge_documents WHERE embedding_model <> $model;";
            AddParameter(selectCommand, "$model", _options.EmbeddingModelName);

            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                staleIds.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        if (staleIds.Count > 0)
        {
            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = """
                                        UPDATE knowledge_documents
                                        SET status = $status, failure_reason = NULL, updated_at_utc = $updated_at_utc
                                        WHERE embedding_model <> $model;
                                        """;
            AddParameter(updateCommand, "$status", KnowledgeDocumentStatus.Pending.ToString());
            AddParameter(updateCommand, "$updated_at_utc", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            AddParameter(updateCommand, "$model", _options.EmbeddingModelName);
            _ = await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return staleIds;
    }

    private static async Task<IReadOnlyList<KnowledgeDocumentChunkView>> ReadChunksAsync(DbConnection connection, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT chunk_index, heading_path, content
                              FROM knowledge_document_chunks
                              WHERE document_id = $document_id
                              ORDER BY chunk_index ASC;
                              """;
        AddParameter(command, "$document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var chunks = new List<KnowledgeDocumentChunkView>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            chunks.Add(new KnowledgeDocumentChunkView(
                reader.GetInt32(0),
                await reader.IsDBNullAsync(ordinal: 1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(1),
                reader.GetString(2)));
        }

        return chunks;
    }

    private async Task<string> DecryptNameAsync(DbDataReader reader, int ordinal, Guid documentId, CancellationToken cancellationToken)
    {
        var encrypted = await reader.GetFieldValueAsync<byte[]>(ordinal, cancellationToken).ConfigureAwait(false);
        return _dbContext.DecryptKnowledgeFileName(encrypted, documentId);
    }

    private bool IsStaleModel(string embeddingModel)
    {
        return !string.Equals(embeddingModel, _options.EmbeddingModelName, StringComparison.Ordinal);
    }

    private static KnowledgeDocumentStatus ParseStatus(string status)
    {
        return Enum.TryParse<KnowledgeDocumentStatus>(status, out var parsed) ? parsed : KnowledgeDocumentStatus.Pending;
    }
}
