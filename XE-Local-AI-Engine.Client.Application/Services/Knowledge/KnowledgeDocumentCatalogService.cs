namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Default <see cref="IKnowledgeDocumentCatalogService" />. Reads and lightly mutates the <c>knowledge_documents</c>
///     catalog over the raw-SQL path (matching the rest of the knowledge lane), decrypting the display name via the
///     matching <see cref="NodeChatDbContext" /> helper. The stale-model flag compares each row's stored embedding model
///     against the RESOLVED embedding model name (from <see cref="IEmbeddingModelResolver" />, computed once per call) —
///     the same identity the ingestion and search lanes use as the vector scope key — so a same-dimension model swap that
///     leaves <see cref="KnowledgeBaseOptions.EmbeddingModelName" /> unchanged is still detected as stale. If the provider
///     or model cannot be resolved, it falls back to the configured name so staleness never throws. Scoped: it uses the
///     request-scoped db context.
/// </summary>
public sealed class KnowledgeDocumentCatalogService : IKnowledgeDocumentCatalogService
{
    private readonly NodeChatDbContext _dbContext;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly IEmbeddingModelResolver _embeddingModelResolver;
    private readonly KnowledgeBaseOptions _options;
    private readonly TimeProvider _timeProvider;

    public KnowledgeDocumentCatalogService(NodeChatDbContext dbContext,
        ILocalModelProviderResolver providerResolver,
        IEmbeddingModelResolver embeddingModelResolver,
        IOptions<KnowledgeBaseOptions> options,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _embeddingModelResolver = embeddingModelResolver ?? throw new ArgumentNullException(nameof(embeddingModelResolver));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<IReadOnlyList<KnowledgeDocumentSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var resolvedModel = await ResolveEmbeddingModelAsync(cancellationToken).ConfigureAwait(false);

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
            var status = ParseStatus(reader.GetString(2));
            var embeddingModel = reader.GetString(5);
            documents.Add(new KnowledgeDocumentSummary(
                documentId,
                displayName,
                status,
                await reader.IsDBNullAsync(ordinal: 3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
                reader.GetInt32(4),
                embeddingModel,
                IsStaleModel(status, embeddingModel, resolvedModel),
                reader.GetInt64(6),
                reader.GetInt64(7)));
        }

        return documents;
    }

    public async Task<KnowledgeDocumentDetail?> GetAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var resolvedModel = await ResolveEmbeddingModelAsync(cancellationToken).ConfigureAwait(false);

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
            var status = ParseStatus(reader.GetString(2));
            var embeddingModel = reader.GetString(5);
            detail = new KnowledgeDocumentDetail(
                documentId,
                displayName,
                status,
                await reader.IsDBNullAsync(ordinal: 3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
                reader.GetInt32(4),
                embeddingModel,
                IsStaleModel(status, embeddingModel, resolvedModel),
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
        // Resolve the current embedding model once; every row whose stored embedding_model differs from it is stale.
        var resolvedModel = await ResolveEmbeddingModelAsync(cancellationToken).ConfigureAwait(false);

        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Only INDEXED documents can be stale: they carry committed vectors built by a specific model. A non-indexed row
        // still holds the upload-time placeholder (the configured name written by the blob store), which is not a
        // vector-identity and must never trigger a reset of an in-flight or pending document.
        var indexedStatus = KnowledgeDocumentStatus.Indexed.ToString();

        var staleIds = new List<Guid>();
        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = transaction;
            selectCommand.CommandText = "SELECT document_id FROM knowledge_documents WHERE status = $indexed AND embedding_model <> $model;";
            AddParameter(selectCommand, "$indexed", indexedStatus);
            AddParameter(selectCommand, "$model", resolvedModel);

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
                                        WHERE status = $indexed AND embedding_model <> $model;
                                        """;
            AddParameter(updateCommand, "$status", KnowledgeDocumentStatus.Pending.ToString());
            AddParameter(updateCommand, "$updated_at_utc", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            AddParameter(updateCommand, "$indexed", indexedStatus);
            AddParameter(updateCommand, "$model", resolvedModel);
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

    // Resolves the embedding model the same way the ingestion/search lanes do (provider → IEmbeddingModelResolver), so
    // staleness compares each stored name against the model that would actually build vectors now. The resolver already
    // degrades transport failures to the configured name; a missing/unregistered provider surfaces as
    // InvalidOperationException here and also falls back to the configured name so staleness never throws. A genuine
    // caller cancellation propagates.
    private async Task<string> ResolveEmbeddingModelAsync(CancellationToken cancellationToken)
    {
        try
        {
            var provider = _providerResolver.ResolveProvider(_options.EmbeddingProviderName);
            return await _embeddingModelResolver.ResolveAsync(provider, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return _options.EmbeddingModelName;
        }
    }

    // A document is stale only when it is INDEXED (its stored embedding_model names the model that actually built its
    // committed vectors) and that stored name differs from the currently resolved model. A non-indexed row still carries
    // the upload-time placeholder, which is not a vector identity and is never treated as stale.
    private static bool IsStaleModel(KnowledgeDocumentStatus status, string embeddingModel, string resolvedModel)
    {
        return status == KnowledgeDocumentStatus.Indexed
               && !string.Equals(embeddingModel, resolvedModel, StringComparison.Ordinal);
    }

    private static KnowledgeDocumentStatus ParseStatus(string status)
    {
        return Enum.TryParse<KnowledgeDocumentStatus>(status, out var parsed) ? parsed : KnowledgeDocumentStatus.Pending;
    }
}
