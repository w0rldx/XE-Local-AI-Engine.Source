namespace XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Default <see cref="IKnowledgeDocumentCatalogService" />: reads and lightly mutates the <c>knowledge_documents</c>
///     catalog over the raw-SQL path, decrypting the display name via the matching <see cref="NodeChatDbContext" />
///     helper.
/// </summary>
/// <remarks>
///     The stale-model flag compares each row's stored embedding model against the RESOLVED name
///     (<see cref="IEmbeddingModelResolver" />, computed once per call) — the identity the ingestion and search lanes use
///     as the vector scope key — so a same-dimension model swap that leaves
///     <see cref="KnowledgeBaseOptions.EmbeddingModelName" /> unchanged is still detected. Staleness is evaluated ONLY on
///     a confident resolution: <c>docs/wiki/15-knowledge-base.md</c> ("Ingestion pipeline"). Scoped to the db context.
/// </remarks>
public sealed class KnowledgeDocumentCatalogService : IKnowledgeDocumentCatalogService
{
    // Ingestion starts after which startup recovery stops re-queuing a document and fails it instead.
    private const int MaxInterruptedAttempts = 3;
    internal const string InterruptedRepeatedlyReason = "Ingestion was interrupted repeatedly and was stopped.";

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
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
        ArgumentNullException.ThrowIfNull(providerResolver);
        _providerResolver = providerResolver;
        ArgumentNullException.ThrowIfNull(embeddingModelResolver);
        _embeddingModelResolver = embeddingModelResolver;
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<KnowledgeDocumentSummary>> ListAsync(CancellationToken cancellationToken)
    {
        return await ListCoreAsync(new DocumentFilter(), limit: -1, offset: 0, await ResolveEmbeddingModelAsync(cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<KnowledgeDocumentSummary>> ListAsync(string collectionId, CancellationToken cancellationToken)
    {
        if (!KnowledgeCollectionScope.TryNormalize(collectionId, out var normalizedCollectionId))
        {
            return [];
        }

        return await ListCoreAsync(new DocumentFilter
            {
                CollectionId = normalizedCollectionId
            },
            limit: -1,
            offset: 0,
            await ResolveEmbeddingModelAsync(cancellationToken),
            cancellationToken);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only the filter's fixed clause and internally generated placeholder names are interpolated; every value is bound.")]
    [SuppressMessage("Security Hotspot", "S2077:Formatting SQL queries is security-sensitive",
        Justification = "Only the filter's fixed clause and internally generated placeholder names are interpolated; every value is bound.")]
    public async Task<KnowledgeDocumentListing> ListWithEmbeddingStatusAsync(KnowledgeDocumentListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit < 1 || query.Offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "A page holds at least one document and starts at a non-negative offset.");
        }

        var resolution = await ResolveEmbeddingModelAsync(cancellationToken);
        string? normalizedCollectionId = null;

        // An unusable namespace holds no documents, but the embedding verdict is node-wide and still gates the upload.
        if (!string.IsNullOrWhiteSpace(query.CollectionId) && !KnowledgeCollectionScope.TryNormalize(query.CollectionId, out normalizedCollectionId))
        {
            return new KnowledgeDocumentListing
            {
                Items = [],
                TotalCount = 0,
                HasStaleDocuments = false,
                HasSearchableDocuments = false,
                Embedding = resolution
            };
        }

        var filter = new DocumentFilter
        {
            CollectionId = normalizedCollectionId,
            DocumentIds = query.DocumentIds
        };
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        // The page alone cannot answer what the whole filtered set holds, so the count and the two corpus facts the
        // page's callers act on come from one aggregate over plaintext columns: nothing here is decrypted.
        int totalCount;
        bool hasSearchable;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT COUNT(*), COALESCE(MAX(status = $indexed OR chunk_count > 0), 0) FROM knowledge_documents {AddFilter(command, filter)};";
            AddParameter(command, "$indexed", KnowledgeDocumentStatus.Indexed.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            _ = await reader.ReadAsync(cancellationToken);
            totalCount = reader.GetInt32(0);
            hasSearchable = reader.GetInt64(1) != 0;
        }

        return new KnowledgeDocumentListing
        {
            Items = await ListCoreAsync(filter, query.Limit, query.Offset, resolution, cancellationToken),
            TotalCount = totalCount,
            HasStaleDocuments = await HasStaleDocumentsAsync(connection, filter, resolution, cancellationToken),
            HasSearchableDocuments = hasSearchable,
            Embedding = resolution
        };
    }

    public async Task<IReadOnlyList<KnowledgeDocumentSummary>> ListAsync(string collectionId,
        string sourceKind,
        string sourceId,
        CancellationToken cancellationToken)
    {
        if (!KnowledgeCollectionScope.TryNormalize(collectionId, out var normalizedCollectionId)
            || string.IsNullOrWhiteSpace(sourceKind)
            || string.IsNullOrWhiteSpace(sourceId))
        {
            return [];
        }

        return await ListCoreAsync(new DocumentFilter
            {
                CollectionId = normalizedCollectionId,
                SourceKind = sourceKind,
                SourceId = sourceId
            },
            limit: -1,
            offset: 0,
            await ResolveEmbeddingModelAsync(cancellationToken),
            cancellationToken);
    }

    // Staleness needs the C# vector-policy check, so it is judged over the DISTINCT index identities (a handful, however
    // many documents share them) rather than per row.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only the filter's fixed clause and internally generated placeholder names are interpolated; every value is bound.")]
    [SuppressMessage("Security Hotspot", "S2077:Formatting SQL queries is security-sensitive",
        Justification = "Only the filter's fixed clause and internally generated placeholder names are interpolated; every value is bound.")]
    private async Task<bool> HasStaleDocumentsAsync(DbConnection connection,
        DocumentFilter filter,
        EmbeddingModelResolution resolution,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
                               SELECT DISTINCT embedding_model, vector_identity, vector_dim, parser_version, chunker_version
                               FROM knowledge_documents {AddFilter(command, filter)} AND status = $indexed;
                               """;
        AddParameter(command, "$indexed", KnowledgeDocumentStatus.Indexed.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (IsStaleIndex(KnowledgeDocumentStatus.Indexed,
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    resolution))
            {
                return true;
            }
        }

        return false;
    }

    // Returns the WHERE clause for the filter and binds its parameters. Document ids get generated placeholder names,
    // never interpolated values; SQLite reads an empty IN list as false.
    private static string AddFilter(DbCommand command, DocumentFilter filter)
    {
        AddParameter(command, "$collection_id", filter.CollectionId);
        AddParameter(command, "$source_kind", filter.SourceKind);
        AddParameter(command, "$source_id", filter.SourceId);
        const string where = """
                             WHERE ($collection_id IS NULL OR collection_id = $collection_id)
                               AND ($source_kind IS NULL OR source_kind = $source_kind)
                               AND ($source_id IS NULL OR source_id = $source_id)
                             """;
        if (filter.DocumentIds is not { } documentIds)
        {
            return where;
        }

        var names = new string[documentIds.Count];
        for (var index = 0; index < documentIds.Count; index++)
        {
            names[index] = "$document_id_" + index.ToString(CultureInfo.InvariantCulture);
            AddParameter(command, names[index], documentIds[index]);
        }

        return where + " AND document_id IN (" + string.Join(", ", names) + ")";
    }

    // limit -1 is SQLite's "no limit": the internal source and collection reads still want every row.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only the filter's fixed clause and internally generated placeholder names are interpolated; every value is bound.")]
    [SuppressMessage("Security Hotspot", "S2077:Formatting SQL queries is security-sensitive",
        Justification = "Only the filter's fixed clause and internally generated placeholder names are interpolated; every value is bound.")]
    private async Task<IReadOnlyList<KnowledgeDocumentSummary>> ListCoreAsync(DocumentFilter filter,
        int limit,
        int offset,
        EmbeddingModelResolution resolution,
        CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
                               SELECT document_id, original_file_name, status, failure_reason, chunk_count, embedding_model,
                                      vector_identity, vector_dim, size_bytes, created_at_utc, collection_id, source_path,
                                      source_kind, parser_version, chunker_version
                               FROM knowledge_documents
                               {AddFilter(command, filter)}
                               ORDER BY created_at_utc DESC, document_id DESC
                               LIMIT $limit OFFSET $offset;
                               """;
        AddParameter(command, "$limit", limit);
        AddParameter(command, "$offset", offset);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var documents = new List<KnowledgeDocumentSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var documentId = Guid.Parse(reader.GetString(0));
            var displayName = await DecryptNameAsync(reader, ordinal: 1, documentId, cancellationToken);
            var status = ParseStatus(reader.GetString(2));
            var embeddingModel = reader.GetString(5);
            documents.Add(new KnowledgeDocumentSummary
            {
                DocumentId = documentId,
                DisplayName = displayName,
                Status = status,
                FailureReason = await reader.IsDBNullAsync(ordinal: 3, cancellationToken) ? null : reader.GetString(3),
                ChunkCount = reader.GetInt32(4),
                EmbeddingModel = embeddingModel,
                StaleModel = IsStaleIndex(status,
                    embeddingModel,
                    reader.GetString(6),
                    reader.GetInt32(7),
                    reader.GetString(13),
                    reader.GetString(14),
                    resolution),
                SizeBytes = reader.GetInt64(8),
                CreatedAtUtc = reader.GetInt64(9),
                CollectionId = reader.GetString(10),
                SourcePath = await reader.IsDBNullAsync(11, cancellationToken) ? null : reader.GetString(11),
                SourceKind = reader.GetString(12)
            });
        }

        return documents;
    }

    private sealed record DocumentFilter
    {
        public string? CollectionId { get; init; }

        public string? SourceKind { get; init; }

        public string? SourceId { get; init; }

        public IReadOnlyList<Guid>? DocumentIds { get; init; }
    }

    public async Task<KnowledgeDocumentDetail> GetAsync(Guid documentId, CancellationToken cancellationToken)
    {
        return await GetCoreAsync(documentId, collectionId: null, cancellationToken) ?? throw NotFound();
    }

    public async Task<KnowledgeDocumentDetail?> GetAsync(Guid documentId, string collectionId, CancellationToken cancellationToken)
    {
        if (!KnowledgeCollectionScope.TryNormalize(collectionId, out var normalizedCollectionId))
        {
            return null;
        }

        return await GetCoreAsync(documentId, normalizedCollectionId, cancellationToken);
    }

    private async Task<KnowledgeDocumentDetail?> GetCoreAsync(Guid documentId, string? collectionId, CancellationToken cancellationToken)
    {
        var resolution = await ResolveEmbeddingModelAsync(cancellationToken);

        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        KnowledgeDocumentDetail? detail;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                                  SELECT document_id, original_file_name, status, failure_reason, chunk_count, embedding_model,
                                         vector_identity, vector_dim, size_bytes, created_at_utc, updated_at_utc,
                                         collection_id, source_path, source_kind, parser_version, chunker_version
                                  FROM knowledge_documents
                                  WHERE document_id = $document_id
                                    AND ($collection_id IS NULL OR collection_id = $collection_id);
                                  """;
            AddParameter(command, "$document_id", documentId);
            AddParameter(command, "$collection_id", collectionId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var displayName = await DecryptNameAsync(reader, ordinal: 1, documentId, cancellationToken);
            var status = ParseStatus(reader.GetString(2));
            var embeddingModel = reader.GetString(5);
            detail = new KnowledgeDocumentDetail
            {
                DocumentId = documentId,
                DisplayName = displayName,
                Status = status,
                FailureReason = await reader.IsDBNullAsync(ordinal: 3, cancellationToken) ? null : reader.GetString(3),
                ChunkCount = reader.GetInt32(4),
                EmbeddingModel = embeddingModel,
                StaleModel = IsStaleIndex(status,
                    embeddingModel,
                    reader.GetString(6),
                    reader.GetInt32(7),
                    reader.GetString(14),
                    reader.GetString(15),
                    resolution),
                SizeBytes = reader.GetInt64(8),
                CreatedAtUtc = reader.GetInt64(9),
                UpdatedAtUtc = reader.GetInt64(10),
                Chunks = [],
                CollectionId = reader.GetString(11),
                SourcePath = await reader.IsDBNullAsync(12, cancellationToken) ? null : reader.GetString(12),
                SourceKind = reader.GetString(13)
            };
        }

        var chunks = await ReadChunksAsync(connection, documentId, cancellationToken);
        return detail with
        {
            Chunks = chunks
        };
    }

    public async Task<KnowledgeDocumentStatus?> GetStatusAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM knowledge_documents WHERE document_id = $document_id;";
        AddParameter(command, "$document_id", documentId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string status ? ParseStatus(status) : null;
    }

    public async Task ResetToPendingAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              UPDATE knowledge_documents
                              SET status = $status, failure_reason = NULL, updated_at_utc = $updated_at_utc,
                                  ingestion_attempts = 0
                              WHERE document_id = $document_id;
                              """;
        AddParameter(command, "$status", KnowledgeDocumentStatus.Pending.ToString());
        AddParameter(command, "$updated_at_utc", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        AddParameter(command, "$document_id", documentId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw NotFound();
        }
    }

    private static KnowledgeDocumentNotFoundException NotFound() =>
        new("The knowledge document was not found.");

    public async Task<IReadOnlyList<Guid>> ResetStaleDocumentsToPendingAsync(CancellationToken cancellationToken)
    {
        // Resolve the current embedding model once. Model/vector comparisons require a confident resolution, but
        // parser/chunker version changes are local deterministic facts and must still reindex during a provider outage.
        var resolution = await ResolveEmbeddingModelAsync(cancellationToken);

        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Only INDEXED documents can be stale: they carry committed vectors built by a specific model. A non-indexed row
        // still holds the upload-time placeholder, which is no vector identity and must never reset an in-flight document.
        var indexedStatus = KnowledgeDocumentStatus.Indexed.ToString();

        var staleIds = new List<Guid>();
        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = transaction;
            selectCommand.CommandText = """
                                        SELECT document_id, embedding_model, vector_identity, vector_dim,
                                               parser_version, chunker_version
                                        FROM knowledge_documents
                                        WHERE status = $indexed;
                                        """;
            AddParameter(selectCommand, "$indexed", indexedStatus);

            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (IsStaleIndex(KnowledgeDocumentStatus.Indexed,
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt32(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        resolution))
                {
                    staleIds.Add(Guid.Parse(reader.GetString(0)));
                }
            }
        }

        foreach (var staleId in staleIds)
        {
            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = """
                                        UPDATE knowledge_documents
                                        SET status = $status, failure_reason = NULL, updated_at_utc = $updated_at_utc,
                                            ingestion_attempts = 0
                                        WHERE status = $indexed AND document_id = $document_id;
                                        """;
            AddParameter(updateCommand, "$status", KnowledgeDocumentStatus.Pending.ToString());
            AddParameter(updateCommand, "$updated_at_utc", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            AddParameter(updateCommand, "$indexed", indexedStatus);
            AddParameter(updateCommand, "$document_id", staleId);
            _ = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return staleIds;
    }

    public async Task<IReadOnlyList<Guid>> ResetNonTerminalToPendingAsync(CancellationToken cancellationToken)
    {
        // Startup recovery: a non-terminal document (Pending/Extracting/Chunking/Embedding) existed only in the lost
        // in-memory queue. Re-running is safe — the state machine restarts and the index writer purges partial rows first.
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var indexedStatus = KnowledgeDocumentStatus.Indexed.ToString();
        var failedStatus = KnowledgeDocumentStatus.Failed.ToString();

        var interruptedIds = new List<Guid>();
        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = transaction;
            selectCommand.CommandText = """
                                        SELECT document_id FROM knowledge_documents
                                        WHERE status <> $indexed AND status <> $failed AND ingestion_attempts < $max_attempts;
                                        """;
            AddParameter(selectCommand, "$indexed", indexedStatus);
            AddParameter(selectCommand, "$failed", failedStatus);
            AddParameter(selectCommand, "$max_attempts", MaxInterruptedAttempts);

            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                interruptedIds.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        // A document whose every start ended with the process gone (a crash, an OOM kill) would otherwise re-run at every
        // boot forever: past the limit it fails for good, and only an explicit retry (which zeroes the count) re-runs it.
        await using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = """
                                        UPDATE knowledge_documents
                                        SET status = CASE WHEN ingestion_attempts >= $max_attempts THEN $failed ELSE $pending END,
                                            failure_reason = CASE WHEN ingestion_attempts >= $max_attempts THEN $reason ELSE NULL END,
                                            updated_at_utc = $updated_at_utc
                                        WHERE status <> $indexed AND status <> $failed;
                                        """;
            AddParameter(updateCommand, "$pending", KnowledgeDocumentStatus.Pending.ToString());
            AddParameter(updateCommand, "$reason", InterruptedRepeatedlyReason);
            AddParameter(updateCommand, "$max_attempts", MaxInterruptedAttempts);
            AddParameter(updateCommand, "$updated_at_utc", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            AddParameter(updateCommand, "$indexed", indexedStatus);
            AddParameter(updateCommand, "$failed", failedStatus);
            _ = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return interruptedIds;
    }

    public async Task<IReadOnlyList<Guid>> ListPendingDocumentIdsAsync(CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document_id FROM knowledge_documents WHERE status = $pending;";
        AddParameter(command, "$pending", KnowledgeDocumentStatus.Pending.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var pendingIds = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            pendingIds.Add(Guid.Parse(reader.GetString(0)));
        }

        return pendingIds;
    }

    public async Task<IReadOnlyList<string>> ListIndexedEmbeddingModelsAsync(int limit, CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT embedding_model FROM knowledge_documents WHERE status = $indexed LIMIT $limit;";
        AddParameter(command, "$indexed", KnowledgeDocumentStatus.Indexed.ToString());
        AddParameter(command, "$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var models = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            models.Add(reader.GetString(0));
        }

        return models;
    }

    public async Task<IReadOnlyList<KnowledgeCollectionSummary>> ListCollectionsAsync(CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT collection_id, COUNT(*) FROM knowledge_documents GROUP BY collection_id ORDER BY collection_id;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var collections = new List<KnowledgeCollectionSummary>
        {
            new()
            {
                CollectionId = KnowledgeCollectionScope.DefaultId,
                DocumentCount = 0
            }
        };
        while (await reader.ReadAsync(cancellationToken))
        {
            var summary = new KnowledgeCollectionSummary
            {
                CollectionId = reader.GetString(0),
                DocumentCount = reader.GetInt32(1)
            };
            if (string.Equals(summary.CollectionId, KnowledgeCollectionScope.DefaultId, StringComparison.Ordinal))
            {
                collections[0] = summary;
            }
            else
            {
                collections.Add(summary);
            }
        }

        return collections;
    }

    private static async Task<IReadOnlyList<KnowledgeDocumentChunkView>> ReadChunksAsync(DbConnection connection, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT chunk_index, heading_path, content, page_number, start_offset, end_offset,
                                     content_kind, source_path, language, symbol
                              FROM knowledge_document_chunks
                              WHERE document_id = $document_id
                              ORDER BY chunk_index ASC;
                              """;
        AddParameter(command, "$document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var chunks = new List<KnowledgeDocumentChunkView>();
        while (await reader.ReadAsync(cancellationToken))
        {
            chunks.Add(new KnowledgeDocumentChunkView
            {
                ChunkIndex = reader.GetInt32(0),
                HeadingPath = await reader.IsDBNullAsync(ordinal: 1, cancellationToken) ? null : reader.GetString(1),
                Content = reader.GetString(2),
                PageNumber = await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetInt32(3),
                StartOffset = reader.GetInt32(4),
                EndOffset = reader.GetInt32(5),
                ContentKind = reader.GetString(6),
                SourcePath = await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetString(7),
                Language = await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetString(8),
                Symbol = await reader.IsDBNullAsync(9, cancellationToken) ? null : reader.GetString(9)
            });
        }

        return chunks;
    }

    private async Task<string> DecryptNameAsync(DbDataReader reader, int ordinal, Guid documentId, CancellationToken cancellationToken)
    {
        var encrypted = await reader.GetFieldValueAsync<byte[]>(ordinal, cancellationToken);
        return _dbContext.DecryptKnowledgeFileName(encrypted, documentId);
    }

    // Resolves the embedding model exactly as the ingestion and search lanes do, so staleness compares each stored name
    // against the model that would build vectors now; a missing provider folds into the same NOT-confident outcome.
    private async Task<EmbeddingModelResolution> ResolveEmbeddingModelAsync(CancellationToken cancellationToken)
    {
        try
        {
            var provider = _providerResolver.ResolveProvider(_options.EmbeddingProviderName);
            return await _embeddingModelResolver.ResolveAsync(provider, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return new EmbeddingModelResolution
            {
                Name = _options.EmbeddingModelName,
                IsConfident = false
            };
        }
    }

    // A document is stale only when it is INDEXED, the current resolution is CONFIDENT (an installed model was actually
    // matched), and the stored name differs: a mere fallback would flag and reset the whole corpus during an outage.
    private bool IsStaleIndex(KnowledgeDocumentStatus status,
        string embeddingModel,
        string vectorIdentity,
        int vectorDimension,
        string parserVersion,
        string chunkerVersion,
        EmbeddingModelResolution resolution)
    {
        return status == KnowledgeDocumentStatus.Indexed
               && (!string.Equals(parserVersion, KnowledgeIndexVersions.Parser, StringComparison.Ordinal)
                   || !string.Equals(chunkerVersion, KnowledgeIndexVersions.Chunker, StringComparison.Ordinal)
                   || (resolution.IsConfident
                       && (!string.Equals(embeddingModel, resolution.Name, StringComparison.Ordinal)
                           || !KnowledgeEmbeddingVectorPolicy.MatchesCurrentPolicy(vectorIdentity,
                               vectorDimension,
                               resolution,
                               _options.EmbeddingVectorMode))));
    }

    private static KnowledgeDocumentStatus ParseStatus(string status)
    {
        return Enum.TryParse<KnowledgeDocumentStatus>(status, out var parsed) ? parsed : KnowledgeDocumentStatus.Pending;
    }
}
