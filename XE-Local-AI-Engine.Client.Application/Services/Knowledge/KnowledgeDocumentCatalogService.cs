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
///     leaves <see cref="KnowledgeBaseOptions.EmbeddingModelName" /> unchanged is still detected as stale. Staleness is
///     evaluated ONLY when the resolver's outcome is confident (an installed model was actually matched). When resolution
///     is not confident (the provider could not be reached, or the resolver could not match anything installed and fell
///     back to the configured name), staleness is skipped entirely rather than compared against that fallback name — on a
///     llama.cpp node the stored <c>embedding_model</c> is a resolved GGUF name that never equals the plain configured
///     name, so comparing against a mere fallback during a transient outage would misclassify (and
///     <see cref="ResetStaleDocumentsToPendingAsync" /> would reset) the ENTIRE indexed corpus instead of leaving it
///     untouched. Scoped: it uses the request-scoped db context.
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
        var resolution = await ResolveEmbeddingModelAsync(cancellationToken).ConfigureAwait(false);

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
            documents.Add(new KnowledgeDocumentSummary(documentId,
                displayName,
                status,
                await reader.IsDBNullAsync(ordinal: 3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
                reader.GetInt32(4),
                embeddingModel,
                IsStaleModel(status, embeddingModel, resolution),
                reader.GetInt64(6),
                reader.GetInt64(7)));
        }

        return documents;
    }

    public async Task<KnowledgeDocumentDetail?> GetAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var resolution = await ResolveEmbeddingModelAsync(cancellationToken).ConfigureAwait(false);

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
            detail = new KnowledgeDocumentDetail(documentId,
                displayName,
                status,
                await reader.IsDBNullAsync(ordinal: 3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
                reader.GetInt32(4),
                embeddingModel,
                IsStaleModel(status, embeddingModel, resolution),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                Chunks: []);
        }

        var chunks = await ReadChunksAsync(connection, documentId, cancellationToken).ConfigureAwait(false);
        return detail with
        {
            Chunks = chunks
        };
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
        // Resolve the current embedding model once; every INDEXED row whose stored embedding_model differs from it is
        // stale. When resolution is not confident (transient provider outage, or nothing installed matched), never
        // touch the table: comparing against a mere fallback name would reset the entire indexed corpus during an
        // outage instead of leaving it untouched, and a re-ingest attempted mid-outage would just fail again.
        var resolution = await ResolveEmbeddingModelAsync(cancellationToken).ConfigureAwait(false);
        if (!resolution.IsConfident)
        {
            return [];
        }

        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Only INDEXED documents can be stale: they carry committed vectors built by a specific model. A non-indexed row
        // still holds the upload-time placeholder (the configured name written by the blob store), which is not a
        // vector-identity and must never trigger a reset of an in-flight or pending document.
        var indexedStatus = KnowledgeDocumentStatus.Indexed.ToString();
        var resolvedModel = resolution.Name;

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

    public async Task<IReadOnlyList<Guid>> ResetNonTerminalToPendingAsync(CancellationToken cancellationToken)
    {
        // Startup recovery: a document left in ANY non-terminal status (Pending/Extracting/Chunking/Embedding) by a crash
        // or hard stop only existed in the lost in-memory ingestion queue. Reset it to Pending (clearing any partial
        // failure reason) and return its id so the worker re-dispatches it. Terminal rows (Indexed/Failed) are untouched.
        // Re-running is safe: the state machine restarts from the top and the index writer purges any partial rows before
        // re-inserting, so a document reset mid-state never duplicates or corrupts its projections.
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var indexedStatus = KnowledgeDocumentStatus.Indexed.ToString();
        var failedStatus = KnowledgeDocumentStatus.Failed.ToString();

        var interruptedIds = new List<Guid>();
        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = transaction;
            selectCommand.CommandText = "SELECT document_id FROM knowledge_documents WHERE status <> $indexed AND status <> $failed;";
            AddParameter(selectCommand, "$indexed", indexedStatus);
            AddParameter(selectCommand, "$failed", failedStatus);

            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                interruptedIds.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        if (interruptedIds.Count > 0)
        {
            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = """
                                        UPDATE knowledge_documents
                                        SET status = $status, failure_reason = NULL, updated_at_utc = $updated_at_utc
                                        WHERE status <> $indexed AND status <> $failed;
                                        """;
            AddParameter(updateCommand, "$status", KnowledgeDocumentStatus.Pending.ToString());
            AddParameter(updateCommand, "$updated_at_utc", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            AddParameter(updateCommand, "$indexed", indexedStatus);
            AddParameter(updateCommand, "$failed", failedStatus);
            _ = await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return interruptedIds;
    }

    public async Task<IReadOnlyList<Guid>> ListPendingDocumentIdsAsync(CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document_id FROM knowledge_documents WHERE status = $pending;";
        AddParameter(command, "$pending", KnowledgeDocumentStatus.Pending.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var pendingIds = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            pendingIds.Add(Guid.Parse(reader.GetString(0)));
        }

        return pendingIds;
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
            chunks.Add(new KnowledgeDocumentChunkView(reader.GetInt32(0),
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
    // degrades transport failures to a NOT-confident configured-name fallback; a missing/unregistered provider surfaces
    // as InvalidOperationException here and is folded into the same not-confident outcome so staleness never throws and
    // never compares against a name nothing installed actually matched. A genuine caller cancellation propagates.
    private async Task<EmbeddingModelResolution> ResolveEmbeddingModelAsync(CancellationToken cancellationToken)
    {
        try
        {
            var provider = _providerResolver.ResolveProvider(_options.EmbeddingProviderName);
            return await _embeddingModelResolver.ResolveAsync(provider, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return new EmbeddingModelResolution(_options.EmbeddingModelName, IsConfident: false);
        }
    }

    // A document is stale only when it is INDEXED (its stored embedding_model names the model that actually built its
    // committed vectors), the current resolution is CONFIDENT (an installed model was actually matched — not a mere
    // fallback from an unreachable provider or an unmatched configured name), and the stored name differs from the
    // resolved one. Skipping staleness on a non-confident resolution is the guard against a transient provider outage
    // making the resolver fall back to the plain configured name — on a llama.cpp node the stored name is a resolved
    // GGUF name that never equals that fallback, so comparing against it would flag (and reset) the entire indexed
    // corpus during the outage instead of leaving it untouched.
    private static bool IsStaleModel(KnowledgeDocumentStatus status, string embeddingModel, EmbeddingModelResolution resolution)
    {
        return status == KnowledgeDocumentStatus.Indexed
               && resolution.IsConfident
               && !string.Equals(embeddingModel, resolution.Name, StringComparison.Ordinal);
    }

    private static KnowledgeDocumentStatus ParseStatus(string status)
    {
        return Enum.TryParse<KnowledgeDocumentStatus>(status, out var parsed) ? parsed : KnowledgeDocumentStatus.Pending;
    }
}
