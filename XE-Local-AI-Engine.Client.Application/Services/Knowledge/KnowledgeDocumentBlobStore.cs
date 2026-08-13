namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Providers.Abstractions;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Durable knowledge-base document store. Metadata rows are written/read over the raw-SQL path (matching the node
///     chat persistence path) with the display name encrypted via the matching <see cref="NodeChatDbContext" /> helper;
///     the raw bytes are encrypted on disk by <see cref="UploadedFileBlobProtector" /> under
///     <c>INodeDataDirectory.Root/knowledge-base/documents/</c>. The store is a singleton and opens a fresh scope per
///     database operation. On-disk paths are always derived from the server-generated <c>documentId</c> plus extension;
///     the persisted <c>storage_path</c> is display-only and never used to open a file.
/// </summary>
public sealed class KnowledgeDocumentBlobStore : IKnowledgeDocumentBlobStore
{
    private const string RootFolderName = "knowledge-base";
    private const string DocumentsFolderName = "documents";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INodeDataDirectory _dataDirectory;
    private readonly UploadedFileBlobProtector _blobProtector;
    private readonly TimeProvider _timeProvider;

    public KnowledgeDocumentBlobStore(IServiceScopeFactory scopeFactory,
        INodeDataDirectory dataDirectory,
        INodeSqliteKeyHolder keyHolder,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        ArgumentNullException.ThrowIfNull(keyHolder);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _blobProtector = new UploadedFileBlobProtector(keyHolder);
    }

    public async Task<KnowledgeDocumentAddResult> AddAsync(KnowledgeDocumentInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.OriginalFileName))
        {
            throw new ArgumentException("The knowledge document must have a display name.", nameof(input));
        }

        if (string.IsNullOrWhiteSpace(input.ContentHash))
        {
            throw new ArgumentException("The knowledge document must have a content hash for deduplication.", nameof(input));
        }

        var extension = NormalizeExtension(input.Extension);
        if (!KnowledgeCollectionScope.TryNormalize(input.CollectionId, out var collectionId))
        {
            throw new ArgumentException("The knowledge collection id is invalid.", nameof(input));
        }

        var sourceKind = NormalizeSourceKind(input.SourceKind);
        var sourceId = NormalizeSourceId(input.SourceId);
        var sourcePath = NormalizeSourcePath(input.SourcePath);
        var repositorySource = string.Equals(sourceKind, "repository", StringComparison.Ordinal);
        if (repositorySource && (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(sourcePath)))
        {
            throw new ArgumentException("A repository knowledge document must have a stable source id and normalized source path.", nameof(input));
        }

        var storagePath = string.Concat(input.DocumentId.ToString("D"), extension);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        var encryptedName = dbContext.EncryptKnowledgeFileName(input.OriginalFileName, input.DocumentId);

        // Never check-then-insert. The schema's partial unique indexes select the durable identity: ordinary uploads use
        // collection + content hash, while repository sources use collection + source kind + source id + normalized path.
        int inserted;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                                  INSERT INTO knowledge_documents
                                      (document_id, collection_id, original_file_name, mime_type, extension, size_bytes,
                                       content_hash, storage_path, source_path, source_kind, status, failure_reason,
                                       source_id,
                                       chunk_count, embedding_model, vector_identity, vector_dim, parser_version,
                                       chunker_version, created_at_utc, updated_at_utc)
                                  VALUES
                                      ($document_id, $collection_id, $original_file_name, $mime_type, $extension,
                                       $size_bytes, $content_hash, $storage_path, $source_path, $source_kind, $status,
                                       $failure_reason, $source_id, $chunk_count, $embedding_model, $vector_identity, $vector_dim,
                                       $parser_version, $chunker_version, $created_at_utc, $updated_at_utc)
                                  ON CONFLICT DO NOTHING;
                                  """;
            AddParameter(command, "$document_id", input.DocumentId);
            AddParameter(command, "$collection_id", collectionId);
            AddParameter(command, "$original_file_name", encryptedName);
            AddParameter(command, "$mime_type", input.MimeType);
            AddParameter(command, "$extension", extension);
            AddParameter(command, "$size_bytes", input.SizeBytes);
            AddParameter(command, "$content_hash", input.ContentHash);
            AddParameter(command, "$storage_path", storagePath);
            AddParameter(command, "$source_path", sourcePath);
            AddParameter(command, "$source_kind", sourceKind);
            AddParameter(command, "$source_id", sourceId);
            AddParameter(command, "$status", KnowledgeDocumentStatus.Pending.ToString());
            AddParameter(command, "$failure_reason", value: null);
            AddParameter(command, "$chunk_count", value: 0);
            AddParameter(command, "$embedding_model", input.EmbeddingModel);
            AddParameter(command, "$vector_identity", KnowledgeEmbeddingVectorPolicy.LegacyIdentity);
            AddParameter(command, "$vector_dim", value: 0);
            AddParameter(command, "$parser_version", KnowledgeIndexVersions.Parser);
            AddParameter(command, "$chunker_version", KnowledgeIndexVersions.Chunker);
            AddParameter(command, "$created_at_utc", now);
            AddParameter(command, "$updated_at_utc", now);
            inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (inserted == 0)
        {
            var existing = await SelectDocumentByIdentityAsync(connection,
                    collectionId,
                    sourceKind,
                    sourceId,
                    sourcePath,
                    input.ContentHash,
                    repositorySource,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not { } row || row.DocumentId == Guid.Empty)
            {
                return new KnowledgeDocumentAddResult(Guid.Empty, WasInserted: false);
            }

            if (repositorySource && !string.Equals(row.ContentHash, input.ContentHash, StringComparison.Ordinal))
            {
                await UpdateRepositoryDocumentAsync(connection,
                        dbContext,
                        row,
                        input,
                        collectionId,
                        sourceKind,
                        sourceId!,
                        sourcePath!,
                        extension,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new KnowledgeDocumentAddResult(row.DocumentId, WasInserted: false, WasUpdated: true);
            }

            // Dedupe hit: unchanged content already exists. Do not write a second blob — but if a crash between the
            // original row commit and its blob write left the bytes missing, repair them from the identical content.
            if (!File.Exists(BytesPath(row.DocumentId, row.Extension)))
            {
                await WriteEncryptedBlobAsync(row.DocumentId, row.Extension, input.Content, cancellationToken).ConfigureAwait(false);

                // The prior crash left this row marked Failed (ContentMissingReason) once ingestion could not read its
                // bytes. Now that they are restored, reset it to Pending so UploadKnowledgeDocumentEndpoint re-enqueues it
                // — it only enqueues freshly-inserted or Pending rows, so without this the repaired bytes would never be
                // indexed and every identical re-upload would keep returning the stuck Failed document. Only the
                // missing-blob branch resets; an intact dedupe hit leaves the status untouched.
                await ResetDocumentToPendingAsync(connection, row.DocumentId, now, cancellationToken).ConfigureAwait(false);
            }

            return new KnowledgeDocumentAddResult(row.DocumentId, WasInserted: false);
        }

        // Only write the encrypted blob for a freshly inserted row so a dedupe never orphans bytes on disk. If the blob
        // write fails, roll the row back so we never leave a document row without its bytes.
        try
        {
            await WriteEncryptedBlobAsync(input.DocumentId, extension, input.Content, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DeleteRowAsync(connection, input.DocumentId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new KnowledgeDocumentAddResult(input.DocumentId, WasInserted: true);
    }

    // Encrypts and writes a document blob via a temp sibling + atomic rename, so a crash mid-write never leaves a torn
    // file that a later read would decrypt-fail on. File.Move(overwrite) is a rename within the same directory — atomic
    // on Linux (rename(2)) and Windows (MoveFileEx/MOVEFILE_REPLACE_EXISTING). Encryption is keyed by the document id,
    // so the caller must pass the id that owns the target path (the existing row's id on the dedupe-repair path).
    private async Task WriteEncryptedBlobAsync(Guid documentId, string extension, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DocumentsDirectory());
        var bytesPath = BytesPath(documentId, extension);
        var encryptedBytes = _blobProtector.Encrypt(Guid.Empty, documentId, UploadedFileBlobProtector.FileBytesColumn, content.Span);
        var tempPath = string.Concat(bytesPath, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            await File.WriteAllBytesAsync(tempPath, encryptedBytes, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, bytesPath, overwrite: true);
        }
        catch
        {
            DeleteFileIfExists(tempPath);
            throw;
        }
    }

    public async Task<byte[]?> ReadBytesAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        var extension = await SelectExtensionAsync(connection, documentId, cancellationToken).ConfigureAwait(false);
        if (extension is null)
        {
            return null;
        }

        var bytesPath = BytesPath(documentId, extension);
        if (!File.Exists(bytesPath))
        {
            return null;
        }

        var encrypted = await File.ReadAllBytesAsync(bytesPath, cancellationToken).ConfigureAwait(false);
        return _blobProtector.Decrypt(Guid.Empty, documentId, UploadedFileBlobProtector.FileBytesColumn, encrypted);
    }

    public Task DeleteBytesAsync(Guid documentId, string extension, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(extension);

        // The path is derived purely from the id + normalized extension, mirroring the write path — the caller's stored
        // extension is already normalized, but re-normalize defensively so a raw ".TXT" still resolves the same file.
        DeleteFileIfExists(BytesPath(documentId, NormalizeExtension(extension)));
        return Task.CompletedTask;
    }

    private async Task UpdateRepositoryDocumentAsync(DbConnection connection,
        NodeChatDbContext dbContext,
        (Guid DocumentId, string Extension, string ContentHash) row,
        KnowledgeDocumentInput input,
        string collectionId,
        string sourceKind,
        string sourceId,
        string sourcePath,
        string extension,
        long now,
        CancellationToken cancellationToken)
    {
        var oldBlobPath = BytesPath(row.DocumentId, row.Extension);
        var replacementBlobPath = BytesPath(row.DocumentId, extension);
        var backupBlobPath = string.Concat(oldBlobPath, ".", Guid.NewGuid().ToString("N"), ".backup");
        var backedUpOldBlob = false;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Remove the old searchable projections before the row is marked Pending. With runtime foreign keys OFF,
            // every child lane must be deleted explicitly; deleting chunks also drives the external FTS delete trigger.
            await using (var vectorsCommand = connection.CreateCommand())
            {
                vectorsCommand.Transaction = transaction;
                vectorsCommand.CommandText = "DELETE FROM knowledge_chunk_vectors WHERE document_id = $document_id;";
                AddParameter(vectorsCommand, "$document_id", row.DocumentId);
                _ = await vectorsCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var chunksCommand = connection.CreateCommand())
            {
                chunksCommand.Transaction = transaction;
                chunksCommand.CommandText = "DELETE FROM knowledge_document_chunks WHERE document_id = $document_id;";
                AddParameter(chunksCommand, "$document_id", row.DocumentId);
                _ = await chunksCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var sectionsCommand = connection.CreateCommand())
            {
                sectionsCommand.Transaction = transaction;
                sectionsCommand.CommandText = "DELETE FROM knowledge_document_sections WHERE document_id = $document_id;";
                AddParameter(sectionsCommand, "$document_id", row.DocumentId);
                _ = await sectionsCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var updateCommand = connection.CreateCommand())
            {
                updateCommand.Transaction = transaction;
                updateCommand.CommandText = """
                                            UPDATE knowledge_documents
                                            SET original_file_name = $original_file_name,
                                                mime_type = $mime_type,
                                                extension = $extension,
                                                size_bytes = $size_bytes,
                                                content_hash = $content_hash,
                                                storage_path = $storage_path,
                                                source_path = $source_path,
                                                source_kind = $source_kind,
                                                source_id = $source_id,
                                                status = $status,
                                                failure_reason = NULL,
                                                chunk_count = 0,
                                                embedding_model = $embedding_model,
                                                vector_identity = $vector_identity,
                                                vector_dim = 0,
                                                parser_version = $parser_version,
                                                chunker_version = $chunker_version,
                                                updated_at_utc = $updated_at_utc
                                            WHERE document_id = $document_id
                                              AND collection_id = $collection_id;
                                            """;
                AddParameter(updateCommand,
                    "$original_file_name",
                    dbContext.EncryptKnowledgeFileName(input.OriginalFileName, row.DocumentId));
                AddParameter(updateCommand, "$mime_type", input.MimeType);
                AddParameter(updateCommand, "$extension", extension);
                AddParameter(updateCommand, "$size_bytes", input.SizeBytes);
                AddParameter(updateCommand, "$content_hash", input.ContentHash);
                AddParameter(updateCommand, "$storage_path", string.Concat(row.DocumentId.ToString("D"), extension));
                AddParameter(updateCommand, "$source_path", sourcePath);
                AddParameter(updateCommand, "$source_kind", sourceKind);
                AddParameter(updateCommand, "$source_id", sourceId);
                AddParameter(updateCommand, "$status", KnowledgeDocumentStatus.Pending.ToString());
                AddParameter(updateCommand, "$embedding_model", input.EmbeddingModel);
                AddParameter(updateCommand, "$vector_identity", KnowledgeEmbeddingVectorPolicy.LegacyIdentity);
                AddParameter(updateCommand, "$parser_version", KnowledgeIndexVersions.Parser);
                AddParameter(updateCommand, "$chunker_version", KnowledgeIndexVersions.Chunker);
                AddParameter(updateCommand, "$updated_at_utc", now);
                AddParameter(updateCommand, "$document_id", row.DocumentId);
                AddParameter(updateCommand, "$collection_id", collectionId);
                _ = await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Keep the database transaction open until the replacement blob is durably atomically renamed. When the
            // extension is unchanged, rename the old encrypted blob aside first so a failed write/commit can restore it.
            if (string.Equals(oldBlobPath, replacementBlobPath, StringComparison.Ordinal) && File.Exists(oldBlobPath))
            {
                File.Move(oldBlobPath, backupBlobPath);
                backedUpOldBlob = true;
            }

            await WriteEncryptedBlobAsync(row.DocumentId, extension, input.Content, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            DeleteFileIfExists(backupBlobPath);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            DeleteFileIfExists(replacementBlobPath);
            if (backedUpOldBlob && File.Exists(backupBlobPath))
            {
                File.Move(backupBlobPath, oldBlobPath, overwrite: true);
            }

            throw;
        }

        if (!string.Equals(row.Extension, extension, StringComparison.Ordinal))
        {
            DeleteFileIfExists(BytesPath(row.DocumentId, row.Extension));
        }
    }

    private static async Task<(Guid DocumentId, string Extension, string ContentHash)?> SelectDocumentByIdentityAsync(DbConnection connection,
        string collectionId,
        string sourceKind,
        string? sourceId,
        string? sourcePath,
        string contentHash,
        bool repositorySource,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        AddParameter(command, "$collection_id", collectionId);
        if (repositorySource)
        {
            command.CommandText =
                "SELECT document_id, extension, content_hash FROM knowledge_documents WHERE collection_id = $collection_id AND source_kind = $source_kind AND source_id = $source_id AND source_path = $source_path;";
            AddParameter(command, "$source_kind", sourceKind);
            AddParameter(command, "$source_id", sourceId);
            AddParameter(command, "$source_path", sourcePath);
        }
        else
        {
            command.CommandText =
                "SELECT document_id, extension, content_hash FROM knowledge_documents WHERE collection_id = $collection_id AND source_kind <> 'repository' AND content_hash = $content_hash;";
            AddParameter(command, "$content_hash", contentHash);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var documentId = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false)
            ? Guid.Empty
            : Guid.Parse(reader.GetString(0));
        var extension = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
            ? string.Empty
            : reader.GetString(1);
        var storedContentHash = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
            ? string.Empty
            : reader.GetString(2);
        return (documentId, extension, storedContentHash);
    }

    private static async Task<string?> SelectExtensionAsync(DbConnection connection, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT extension FROM knowledge_documents WHERE document_id = $document_id;";
        AddParameter(command, "$document_id", documentId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : result as string ?? string.Empty;
    }

    private static async Task DeleteRowAsync(DbConnection connection, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM knowledge_documents WHERE document_id = $document_id;";
        AddParameter(command, "$document_id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Resets a repaired dedupe target back to Pending so the upload endpoint re-enqueues it for indexing, clearing the
    // stale content-missing failure and any partial chunk count. Called only after the missing
    // blob has been restored from byte-identical content.
    private static async Task ResetDocumentToPendingAsync(DbConnection connection, Guid documentId, long updatedAtUtc, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              UPDATE knowledge_documents
                              SET status = $status, failure_reason = NULL, chunk_count = 0, updated_at_utc = $updated_at_utc
                              WHERE document_id = $document_id;
                              """;
        AddParameter(command, "$status", KnowledgeDocumentStatus.Pending.ToString());
        AddParameter(command, "$updated_at_utc", updatedAtUtc);
        AddParameter(command, "$document_id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private string DocumentsDirectory()
    {
        return Path.Combine(_dataDirectory.Root, RootFolderName, DocumentsFolderName);
    }

    private string BytesPath(Guid documentId, string extension)
    {
        return Path.Combine(DocumentsDirectory(), string.Concat(documentId.ToString("D"), extension));
    }

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a transient IO error leaves an orphan blob that a later purge also covers.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; a permission error leaves an orphan blob that a later purge also covers.
        }
    }

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "Extensions are ASCII filename suffixes persisted and pathed in a canonical lowercase form, not security identifiers that must round-trip.")]
    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var trimmed = extension.Trim();
        if (!trimmed.StartsWith('.'))
        {
            trimmed = string.Concat(".", trimmed);
        }

        return trimmed.ToLowerInvariant();
    }

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "Source kinds are persisted protocol discriminators with established lowercase values ('upload'/'repository').")]
    private static string NormalizeSourceKind(string sourceKind)
    {
        return string.IsNullOrWhiteSpace(sourceKind) ? "upload" : sourceKind.Trim().ToLowerInvariant();
    }

    private static string? NormalizeSourcePath(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || sourcePath.Any(char.IsControl))
        {
            return null;
        }

        var normalized = sourcePath.Replace(oldChar: '\\', newChar: '/');
        if (normalized.StartsWith('/')
            || (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':'))
        {
            return null;
        }

        var segments = normalized.Split('/');
        if (segments.Any(static segment => segment is "" or "." or ".."))
        {
            return null;
        }

        return string.Join('/', segments).Normalize(NormalizationForm.FormC);
    }

    private static string? NormalizeSourceId(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || sourceId.Any(char.IsControl))
        {
            return null;
        }

        return sourceId.Trim().Normalize(NormalizationForm.FormC);
    }
}
