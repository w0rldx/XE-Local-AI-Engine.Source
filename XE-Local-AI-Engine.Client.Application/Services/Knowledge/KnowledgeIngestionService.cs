namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Default <see cref="IKnowledgeIngestionService" />. Drives one document through the pipeline, advancing
///     <c>knowledge_documents.status</c> at each transition and setting a content-free <c>failure_reason</c> on any step
///     failure. The final Indexed transition is performed atomically by <see cref="IKnowledgeIndexWriter" />. All failure
///     logging is exception-type-only — no chunk or document text ever reaches a log.
/// </summary>
public sealed class KnowledgeIngestionService : IKnowledgeIngestionService
{
    private const string DocumentMissingReason = "The document could not be found.";
    private const string ContentMissingReason = "The document content could not be found.";
    private const string UnsupportedReason = "This file type is not supported for the knowledge base.";
    private const string ExtractionFailedReason = "The document text could not be extracted.";
    private const string EmptyDocumentReason = "No extractable text was found in the document.";
    private const string UnexpectedReason = "Ingestion failed unexpectedly. Retry the upload.";

    private readonly NodeChatDbContext _dbContext;
    private readonly IKnowledgeDocumentBlobStore _blobStore;
    private readonly IDocumentTextExtractor _extractor;
    private readonly IChunkingService _chunkingService;
    private readonly IKnowledgeChunkEmbedder _embedder;
    private readonly IKnowledgeIndexWriter _indexWriter;
    private readonly IKnowledgeIndexingNotifier _notifier;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<KnowledgeIngestionService> _logger;

    public KnowledgeIngestionService(NodeChatDbContext dbContext,
        IKnowledgeDocumentBlobStore blobStore,
        IDocumentTextExtractor extractor,
        IChunkingService chunkingService,
        IKnowledgeChunkEmbedder embedder,
        IKnowledgeIndexWriter indexWriter,
        IKnowledgeIndexingNotifier notifier,
        TimeProvider timeProvider,
        ILogger<KnowledgeIngestionService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _chunkingService = chunkingService ?? throw new ArgumentNullException(nameof(chunkingService));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _indexWriter = indexWriter ?? throw new ArgumentNullException(nameof(indexWriter));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RunAsync(Guid documentId, CancellationToken cancellationToken)
    {
        try
        {
            await IngestAsync(documentId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Genuine cancellation — do not rewrite the document status; let the worker observe the shutdown.
            throw;
        }
        catch (KnowledgeIngestionException exception)
        {
            _logger.LogWarning("Knowledge ingestion failed for document {DocumentId} ({ErrorClass}).", documentId, exception.GetType().Name);
            await SafeFailAsync(documentId, exception.Reason).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Knowledge ingestion failed for document {DocumentId} ({ErrorClass}).", documentId, exception.GetType().Name);
            await SafeFailAsync(documentId, UnexpectedReason).ConfigureAwait(false);
        }
    }

    private async Task IngestAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await SetStatusAsync(documentId, KnowledgeDocumentStatus.Extracting, failureReason: null, cancellationToken).ConfigureAwait(false);

        var extension = await ReadExtensionAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (extension is null)
        {
            await SetStatusAsync(documentId, KnowledgeDocumentStatus.Failed, DocumentMissingReason, cancellationToken).ConfigureAwait(false);
            return;
        }

        var bytes = await _blobStore.ReadBytesAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            await SetStatusAsync(documentId, KnowledgeDocumentStatus.Failed, ContentMissingReason, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var stream = new MemoryStream(bytes, writable: false);
        var extraction = await _extractor.ExtractStructuredAsync(stream, documentId.ToString("D"), extension, cancellationToken).ConfigureAwait(false);
        switch (extraction.Status)
        {
            case DocumentExtractionStatus.Unsupported:
                await SetStatusAsync(documentId, KnowledgeDocumentStatus.Failed, UnsupportedReason, cancellationToken).ConfigureAwait(false);
                return;
            case DocumentExtractionStatus.Failed:
                await SetStatusAsync(documentId, KnowledgeDocumentStatus.Failed, ExtractionFailedReason, cancellationToken).ConfigureAwait(false);
                return;
            case DocumentExtractionStatus.Extracted:
            default:
                break;
        }

        await SetStatusAsync(documentId, KnowledgeDocumentStatus.Chunking, failureReason: null, cancellationToken).ConfigureAwait(false);

        // Token-aware sizing: tighten the chunk budget to the resolved embedding model's context window when it
        // is discoverable, so a chunk and its heading prefix fit the window. Best-effort — a null window (provider down /
        // no advertised context length) falls back to the configured MaxChunkTokens; a provider failure surfaces at the
        // embed step below, not here.
        var embeddingContextWindow = await _embedder.ResolveEmbeddingContextWindowAsync(cancellationToken).ConfigureAwait(false);
        var chunking = _chunkingService.Chunk(extraction.Document!, embeddingContextWindow);
        if (chunking.Chunks.Count == 0)
        {
            await SetStatusAsync(documentId, KnowledgeDocumentStatus.Failed, EmptyDocumentReason, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Delete-vs-ingestion race guard before the expensive embed: if the document was deleted, stop without re-inserting.
        if (!await DocumentExistsAsync(documentId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await SetStatusAsync(documentId, KnowledgeDocumentStatus.Embedding, failureReason: null, cancellationToken).ConfigureAwait(false);

        // Embed the contextual text (heading trail + content) so short chunks under a heading stay retrievable; the stored
        // chunk content remains the plain text.
        var contextualTexts = chunking.Chunks.Select(chunk => chunk.ContextualContent).ToList();
        var embeddingResult = await _embedder.EmbedAsync(contextualTexts, cancellationToken).ConfigureAwait(false);
        if (embeddingResult.Vectors.Count != chunking.Chunks.Count)
        {
            await SetStatusAsync(documentId, KnowledgeDocumentStatus.Failed, UnexpectedReason, cancellationToken).ConfigureAwait(false);
            return;
        }

        var indexChunks = BuildIndexChunks(chunking.Chunks, embeddingResult.Vectors, embeddingResult.Dimension);

        // Stamp the RESOLVED model that actually produced the vectors (not the configured name) as both the document row's
        // embedding_model and every chunk-vector scope key, so a later same-dimension model swap makes stored-name differ
        // from the current resolved name → the catalog flags the document stale → the operator reindexes it.
        var input = new KnowledgeIndexInput(documentId,
            embeddingResult.ResolvedModel,
            embeddingResult.VectorIdentity,
            embeddingResult.Dimension,
            chunking.Sections,
            indexChunks);

        // The writer performs the final Indexed transition atomically. A false result means the document was deleted mid
        // flight (the write was skipped) — there is nothing more to do. On success push the Indexed transition, since the
        // writer sets that status inside its own transaction rather than through SetStatusAsync.
        var indexed = await _indexWriter.WriteAsync(input, cancellationToken).ConfigureAwait(false);
        if (indexed)
        {
            await _notifier.NotifyDocumentChangedAsync(documentId, KnowledgeDocumentStatus.Indexed, cancellationToken).ConfigureAwait(false);
        }
    }

    private static List<KnowledgeIndexChunk> BuildIndexChunks(IReadOnlyList<KnowledgeChunk> chunks, IReadOnlyList<byte[]> embeddings, int dimension)
    {
        var indexChunks = new List<KnowledgeIndexChunk>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            indexChunks.Add(new KnowledgeIndexChunk(chunk.ChunkIndex,
                chunk.SectionOrdinal,
                chunk.Content,
                chunk.HeadingPath,
                chunk.TokenCount,
                embeddings[index],
                dimension));
        }

        return indexChunks;
    }

    private async Task<string?> ReadExtensionAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT extension FROM knowledge_documents WHERE document_id = $document_id;";
        AddParameter(command, "$document_id", documentId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : result as string ?? string.Empty;
    }

    private async Task<bool> DocumentExistsAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM knowledge_documents WHERE document_id = $document_id;";
        AddParameter(command, "$document_id", documentId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null and not DBNull;
    }

    private async Task SetStatusAsync(Guid documentId, KnowledgeDocumentStatus status, string? failureReason, CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              UPDATE knowledge_documents
                              SET status = $status, failure_reason = $failure_reason, updated_at_utc = $updated_at_utc
                              WHERE document_id = $document_id;
                              """;
        AddParameter(command, "$status", status.ToString());
        AddParameter(command, "$failure_reason", failureReason);
        AddParameter(command, "$updated_at_utc", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        AddParameter(command, "$document_id", documentId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Push every non-terminal transition and any Failed transition (the Indexed transition is pushed by the caller,
        // since the index writer sets it inside its own transaction). Best-effort — the notifier never throws.
        await _notifier.NotifyDocumentChangedAsync(documentId, status, cancellationToken).ConfigureAwait(false);
    }

    // Failure-path status write on a fresh token so a status update is never lost to the original cancellation.
    private async Task SafeFailAsync(Guid documentId, string reason)
    {
        try
        {
            await SetStatusAsync(documentId, KnowledgeDocumentStatus.Failed, reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbException exception)
        {
            // The document may have been deleted, or the database may be unavailable; the failure reason is best-effort.
            _logger.LogWarning("Could not persist the Failed status for document {DocumentId} ({ErrorClass}).", documentId, exception.GetType().Name);
        }
    }
}
