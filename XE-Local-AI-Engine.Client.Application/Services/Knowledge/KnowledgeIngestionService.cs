namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Default <see cref="IKnowledgeIngestionService" />: drives one document through the pipeline, advancing
///     <c>knowledge_documents.status</c> at each transition and setting a content-free <c>failure_reason</c> on failure.
/// </summary>
/// <remarks>
///     The final Indexed transition is performed atomically by <see cref="IKnowledgeIndexWriter" />. No chunk or document
///     text ever reaches a log.
/// </remarks>
public sealed class KnowledgeIngestionService : IKnowledgeIngestionService
{
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
    private readonly IKnowledgeChunkEmbeddingCache? _embeddingCache;
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
        ILogger<KnowledgeIngestionService> logger,
        IKnowledgeChunkEmbeddingCache? embeddingCache = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _chunkingService = chunkingService ?? throw new ArgumentNullException(nameof(chunkingService));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _indexWriter = indexWriter ?? throw new ArgumentNullException(nameof(indexWriter));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _embeddingCache = embeddingCache;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RunAsync(Guid documentId, CancellationToken cancellationToken)
    {
        DocumentRevision? revision = null;
        try
        {
            revision = await ReadDocumentRevisionAsync(documentId, cancellationToken);
            if (revision is null)
            {
                return;
            }

            await IngestAsync(documentId, revision.Value, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Genuine cancellation — do not rewrite the document status; let the worker observe the shutdown.
            throw;
        }
        catch (KnowledgeIngestionException exception)
        {
            LogIngestionFailure(documentId, exception);
            await SafeFailAsync(documentId, revision?.ContentHash, exception.Reason);
        }
        catch (Exception exception)
        {
            LogIngestionFailure(documentId, exception);
            await SafeFailAsync(documentId, revision?.ContentHash, UnexpectedReason);
        }
    }

    /// <summary>
    ///     Logs an ingestion failure with the exception TYPE plus the causal chain's messages.
    /// </summary>
    /// <remarks>
    ///     A bare type name leaves the operator without a status, a server response or a failing step, and a TRANSPORT
    ///     failure's message is not document content. The no-content-in-logs rule is preserved by the SOURCES, not by
    ///     suppressing the message: every reason this pipeline raises is a fixed const string (this type's <c>*Reason</c>
    ///     fields and <see cref="KnowledgeChunkEmbedder" />'s), the provider layer sanitizes llama-server's response, and no
    ///     chunk text is interpolated into either. The actionable detail lives in the innermost transport exception.
    /// </remarks>
    private void LogIngestionFailure(Guid documentId, Exception exception)
    {
        var chain = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            chain.Add($"{current.GetType().Name}: {current.Message}");
        }

        _logger.LogWarning("Knowledge ingestion failed for document {DocumentId} ({ErrorClass}): {FailureChain}",
            documentId,
            exception.GetType().Name,
            string.Join(" -> ", chain));
    }

    private async Task IngestAsync(Guid documentId, DocumentRevision revision, CancellationToken cancellationToken)
    {
        if (!await SetStatusAsync(documentId,
                revision.ContentHash,
                KnowledgeDocumentStatus.Extracting,
                failureReason: null,
                cancellationToken))
        {
            return;
        }

        var bytes = await _blobStore.ReadBytesAsync(documentId, cancellationToken);
        if (bytes is null)
        {
            _ = await SetStatusAsync(documentId,
                revision.ContentHash,
                KnowledgeDocumentStatus.Failed,
                ContentMissingReason,
                cancellationToken);
            return;
        }

        using var stream = new MemoryStream(bytes, writable: false);
        var extraction = await _extractor.ExtractStructuredAsync(stream,
            revision.SourcePath ?? documentId.ToString("D"),
            revision.Extension,
            cancellationToken);
        switch (extraction.Status)
        {
            case DocumentExtractionStatus.Unsupported:
                _ = await SetStatusAsync(documentId,
                    revision.ContentHash,
                    KnowledgeDocumentStatus.Failed,
                    UnsupportedReason,
                    cancellationToken);
                return;
            case DocumentExtractionStatus.Failed:
                _ = await SetStatusAsync(documentId,
                    revision.ContentHash,
                    KnowledgeDocumentStatus.Failed,
                    ExtractionFailedReason,
                    cancellationToken);
                return;
            case DocumentExtractionStatus.Extracted:
            default:
                break;
        }

        if (!await SetStatusAsync(documentId,
                revision.ContentHash,
                KnowledgeDocumentStatus.Chunking,
                failureReason: null,
                cancellationToken))
        {
            return;
        }

        // Token-aware sizing: tighten the chunk budget to the resolved embedding window when discoverable, so a chunk and
        // its heading prefix fit it. Best-effort — a null window keeps MaxChunkTokens; a provider failure surfaces at embed.
        var embeddingContextWindow = await _embedder.ResolveEmbeddingContextWindowAsync(cancellationToken);
        var chunking = _chunkingService.Chunk(extraction.Document!, embeddingContextWindow);
        chunking = ApplySourceMetadata(chunking, revision.Extension, revision.SourcePath);
        if (chunking.Chunks.Count == 0)
        {
            _ = await SetStatusAsync(documentId,
                revision.ContentHash,
                KnowledgeDocumentStatus.Failed,
                EmptyDocumentReason,
                cancellationToken);
            return;
        }

        // Repository updates reuse document_id, so existence alone cannot prove this job still owns the current source.
        if (!await DocumentMatchesRevisionAsync(documentId, revision.ContentHash, cancellationToken))
        {
            return;
        }

        if (!await SetStatusAsync(documentId,
                revision.ContentHash,
                KnowledgeDocumentStatus.Embedding,
                failureReason: null,
                cancellationToken))
        {
            return;
        }

        // Embed the contextual text (heading trail + content) so short chunks under a heading stay retrievable; the stored
        // chunk content remains the plain text.
        var embeddingResult = await EmbedWithReuseAsync(chunking.Chunks, cancellationToken);
        if (embeddingResult.Vectors.Count != chunking.Chunks.Count)
        {
            _ = await SetStatusAsync(documentId,
                revision.ContentHash,
                KnowledgeDocumentStatus.Failed,
                UnexpectedReason,
                cancellationToken);
            return;
        }

        var indexChunks = BuildIndexChunks(chunking.Chunks, embeddingResult.Vectors, embeddingResult.Dimension);

        // Stamp the RESOLVED model that produced the vectors, not the configured name, on the document row and every
        // chunk-vector scope key, so a later same-dimension swap has the catalog flag the document stale for a reindex.
        var input = new KnowledgeIndexInput
        {
            DocumentId = documentId,
            SourceContentHash = revision.ContentHash,
            EmbeddingModel = embeddingResult.ResolvedModel,
            VectorIdentity = embeddingResult.VectorIdentity,
            VectorDimension = embeddingResult.Dimension,
            Sections = chunking.Sections,
            Chunks = indexChunks
        };

        // The writer performs the final Indexed transition atomically; false means the document was deleted or replaced
        // mid-flight, so the stale write was skipped. On success push Indexed: the writer set it in its own transaction.
        var indexed = await _indexWriter.WriteAsync(input, cancellationToken);
        if (indexed)
        {
            await _notifier.NotifyDocumentChangedAsync(documentId, KnowledgeDocumentStatus.Indexed, cancellationToken);
        }
    }

    private async Task<KnowledgeEmbeddingResult> EmbedWithReuseAsync(IReadOnlyList<KnowledgeChunk> chunks,
        CancellationToken cancellationToken)
    {
        var contextualTexts = chunks.Select(static chunk => chunk.ContextualContent).ToList();
        if (_embeddingCache is null)
        {
            return await _embedder.EmbedAsync(contextualTexts, cancellationToken);
        }

        var descriptor = await _embedder.ResolveExpectedVectorAsync(cancellationToken);
        if (descriptor is null)
        {
            return await _embedder.EmbedAsync(contextualTexts, cancellationToken);
        }

        var textByKey = new Dictionary<KnowledgeChunkEmbeddingCacheKey, string>();
        var keys = new List<KnowledgeChunkEmbeddingCacheKey>(chunks.Count);
        foreach (var chunk in chunks)
        {
            var key = new KnowledgeChunkEmbeddingCacheKey(chunk.EmbeddingInputHash,
                KnowledgeIndexVersions.Parser,
                KnowledgeIndexVersions.Chunker,
                descriptor.VectorIdentity,
                descriptor.Dimension);
            keys.Add(key);
            textByKey.TryAdd(key, chunk.ContextualContent);
        }

        var vectors = await _embeddingCache.GetOrCreateManyAsync(keys,
            async (missing, token) =>
            {
                var texts = missing.Select(key => textByKey[key]).ToList();
                var generated = await _embedder.EmbedAsync(texts, token);
                if (!string.Equals(generated.ResolvedModel, descriptor.ResolvedModel, StringComparison.Ordinal)
                    || !string.Equals(generated.VectorIdentity, descriptor.VectorIdentity, StringComparison.Ordinal)
                    || generated.Dimension != descriptor.Dimension)
                {
                    throw new KnowledgeIngestionException("The embedding model changed while the document was being indexed. Retry the document.");
                }

                return generated.Vectors;
            },
            cancellationToken);
        return new KnowledgeEmbeddingResult
        {
            Vectors = vectors,
            ResolvedModel = descriptor.ResolvedModel,
            VectorIdentity = descriptor.VectorIdentity,
            Dimension = descriptor.Dimension
        };
    }

    private static List<KnowledgeIndexChunk> BuildIndexChunks(IReadOnlyList<KnowledgeChunk> chunks, IReadOnlyList<byte[]> embeddings, int dimension)
    {
        var indexChunks = new List<KnowledgeIndexChunk>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            indexChunks.Add(new KnowledgeIndexChunk
            {
                ChunkIndex = chunk.ChunkIndex,
                SectionOrdinal = chunk.SectionOrdinal,
                Content = chunk.Content,
                HeadingPath = chunk.HeadingPath,
                TokenCount = chunk.TokenCount,
                Embedding = embeddings[index],
                Dim = dimension,
                PageNumber = chunk.PageNumber,
                StartOffset = chunk.StartOffset,
                EndOffset = chunk.EndOffset,
                ContentKind = chunk.ContentKind,
                SourcePath = chunk.SourcePath,
                Language = chunk.Language,
                Symbol = chunk.Symbol,
                ContentHash = chunk.ContentHash,
                EmbeddingInputHash = chunk.EmbeddingInputHash
            });
        }

        return indexChunks;
    }

    private async Task<DocumentRevision?> ReadDocumentRevisionAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT extension, source_path, content_hash FROM knowledge_documents WHERE document_id = $document_id;";
        AddParameter(command, "$document_id", documentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DocumentRevision(reader.GetString(0),
            await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
            reader.GetString(2));
    }

    private static KnowledgeChunkingResult ApplySourceMetadata(KnowledgeChunkingResult chunking, string extension, string? sourcePath)
    {
        var (contentKind, language) = Classify(extension);
        var enriched = new List<KnowledgeChunk>(chunking.Chunks.Count);
        foreach (var chunk in chunking.Chunks)
        {
            var symbol = contentKind == "code" ? KnowledgeCodeSymbolExtractor.ExtractPrimary(chunk.Content) : null;
            var metadata = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                _ = metadata.Append("Path: ").Append(sourcePath).Append('\n');
            }

            if (!string.IsNullOrWhiteSpace(language))
            {
                _ = metadata.Append("Language: ").Append(language).Append('\n');
            }

            if (!string.IsNullOrWhiteSpace(symbol))
            {
                _ = metadata.Append("Symbol: ").Append(symbol).Append('\n');
            }

            var contextual = metadata.Length == 0
                ? chunk.ContextualContent
                : string.Concat(metadata.ToString(), "\n", chunk.ContextualContent);
            enriched.Add(chunk with
            {
                ContextualContent = contextual,
                ContentKind = contentKind,
                SourcePath = sourcePath,
                Language = language,
                Symbol = symbol,
                EmbeddingInputHash = Hash(contextual)
            });
        }

        return chunking with
        {
            Chunks = enriched
        };
    }

    private static ContentClassification Classify(string extension)
    {
        return extension.ToUpperInvariant() switch
        {
            ".CS" => new ContentClassification("code", "csharp"),
            ".TS" or ".TSX" => new ContentClassification("code", "typescript"),
            ".JS" or ".JSX" or ".MJS" or ".CJS" => new ContentClassification("code", "javascript"),
            ".PY" => new ContentClassification("code", "python"),
            ".JAVA" => new ContentClassification("code", "java"),
            ".GO" => new ContentClassification("code", "go"),
            ".RS" => new ContentClassification("code", "rust"),
            ".CPP" or ".CC" or ".CXX" or ".HPP" or ".HH" => new ContentClassification("code", "cpp"),
            ".C" or ".H" => new ContentClassification("code", "c"),
            ".FS" or ".FSX" => new ContentClassification("code", "fsharp"),
            ".VB" => new ContentClassification("code", "visual-basic"),
            ".SH" or ".BASH" or ".ZSH" => new ContentClassification("code", "shell"),
            ".PS1" => new ContentClassification("code", "powershell"),
            ".SQL" => new ContentClassification("code", "sql"),
            ".LOG" => new ContentClassification("log", null),
            ".MD" or ".MARKDOWN" or ".HTML" or ".HTM" or ".XAML" => new ContentClassification("markup", null),
            ".JSON" or ".JSONC" or ".XML" or ".YAML" or ".YML" or ".TOML" or ".INI" or ".CFG" or ".CONF" => new ContentClassification("structured", null),
            _ => new ContentClassification("text", null)
        };
    }

    private static string Hash(string content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private async Task<bool> DocumentMatchesRevisionAsync(Guid documentId,
        string contentHash,
        CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM knowledge_documents WHERE document_id = $document_id AND content_hash = $content_hash;";
        AddParameter(command, "$document_id", documentId);
        AddParameter(command, "$content_hash", contentHash);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null and not DBNull;
    }

    private async Task<bool> SetStatusAsync(Guid documentId,
        string contentHash,
        KnowledgeDocumentStatus status,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              UPDATE knowledge_documents
                              SET status = $status, failure_reason = $failure_reason, updated_at_utc = $updated_at_utc
                              WHERE document_id = $document_id
                                AND content_hash = $content_hash;
                              """;
        AddParameter(command, "$status", status.ToString());
        AddParameter(command, "$failure_reason", failureReason);
        AddParameter(command, "$updated_at_utc", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        AddParameter(command, "$document_id", documentId);
        AddParameter(command, "$content_hash", contentHash);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (!changed)
        {
            return false;
        }

        // Push every non-terminal transition and any Failed transition (the Indexed transition is pushed by the caller,
        // since the index writer sets it inside its own transaction). Best-effort — the notifier never throws.
        await _notifier.NotifyDocumentChangedAsync(documentId, status, cancellationToken);
        return true;
    }

    // Failure-path status write on a fresh token so a status update is never lost to the original cancellation.
    private async Task SafeFailAsync(Guid documentId, string? contentHash, string reason)
    {
        if (contentHash is null)
        {
            return;
        }

        try
        {
            _ = await SetStatusAsync(documentId,
                contentHash,
                KnowledgeDocumentStatus.Failed,
                reason,
                CancellationToken.None);
        }
        catch (DbException exception)
        {
            // The document may have been deleted, or the database may be unavailable; the failure reason is best-effort.
            _logger.LogWarning("Could not persist the Failed status for document {DocumentId} ({ErrorClass}).", documentId, exception.GetType().Name);
        }
    }

    private readonly record struct DocumentRevision(string Extension, string? SourcePath, string ContentHash);

    // How a chunk's source file is labelled for retrieval: the coarse kind ("code", "log", "markup", "structured",
    // "text") plus the concrete language when the extension names one.
    private readonly record struct ContentClassification(string ContentKind, string? Language);
}
