namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Providers.Abstractions;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>Durable knowledge-base document store.</summary>
/// <remarks>
///     Metadata rows are written and read over the raw-SQL path, matching the node chat persistence path, with the
///     display name encrypted via the matching <see cref="NodeChatDbContext" /> helper; the raw bytes are encrypted on
///     disk by <see cref="UploadedFileBlobProtector" /> under <c>INodeDataDirectory.Root/knowledge-base/documents/</c>.
///     The store is a singleton and opens a fresh scope per database operation. On-disk paths are always derived from
///     the server-generated <c>documentId</c> plus extension; the persisted <c>storage_path</c> is display-only.
/// </remarks>
public sealed class KnowledgeDocumentBlobStore : IKnowledgeDocumentBlobStore
{
    private const string RootFolderName = "knowledge-base";
    private const string DocumentsFolderName = "documents";
    private const string TempSuffix = ".tmp";
    private const string BackupSuffix = ".backup";

    /// <summary>How long an interrupted write's sibling is spared, so the sweep can never race a write in flight.</summary>
    /// <remarks>
    ///     This store holds no lock a sweeper could take, so age is the only signal that a <c>.tmp</c>/<c>.backup</c>
    ///     sibling belongs to a dead writer rather than a live one. Comfortably longer than any single blob write, and
    ///     the same window <c>RetentionSweeperService</c> gives an orphaned artifact scope for the same reason.
    /// </remarks>
    private static readonly TimeSpan InterruptedWriteGrace = TimeSpan.FromMinutes(15);

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
        await OpenIfNeededAsync(connection, cancellationToken);

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
            inserted = await command.ExecuteNonQueryAsync(cancellationToken);
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
                    cancellationToken);
            if (existing is not { } row || row.DocumentId == Guid.Empty)
            {
                return new KnowledgeDocumentAddResult { DocumentId = Guid.Empty, WasInserted = false };
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
                        cancellationToken);
                return new KnowledgeDocumentAddResult { DocumentId = row.DocumentId, WasInserted = false, WasUpdated = true };
            }

            // Dedupe hit: unchanged content already exists. Do not write a second blob — but if a crash between the
            // original row commit and its blob write left the bytes missing, repair them from the identical content.
            if (!File.Exists(BytesPath(row.DocumentId, row.Extension)))
            {
                await WriteEncryptedBlobAsync(row.DocumentId, row.Extension, input.Content, cancellationToken);

                // Ingestion that could not read the bytes left this row Failed (ContentMissingReason); with them restored,
                // reset to Pending so the upload endpoint re-enqueues it. Only this branch resets, a dedupe hit does not.
                await ResetDocumentToPendingAsync(connection, row.DocumentId, now, cancellationToken);
            }

            return new KnowledgeDocumentAddResult { DocumentId = row.DocumentId, WasInserted = false };
        }

        // Only write the encrypted blob for a freshly inserted row so a dedupe never orphans bytes on disk. If the blob
        // write fails, roll the row back so we never leave a document row without its bytes.
        try
        {
            await WriteEncryptedBlobAsync(input.DocumentId, extension, input.Content, cancellationToken);
        }
        catch
        {
            await DeleteRowAsync(connection, input.DocumentId, CancellationToken.None);
            throw;
        }

        return new KnowledgeDocumentAddResult { DocumentId = input.DocumentId, WasInserted = true };
    }

    /// <summary>Encrypts and writes a document blob via a temp sibling plus an atomic rename.</summary>
    /// <remarks>
    ///     The rename means a crash mid-write never leaves a torn file that a later read would decrypt-fail on:
    ///     <c>File.Move(overwrite)</c> within one directory is atomic on Linux (<c>rename(2)</c>) and on Windows
    ///     (<c>MoveFileEx</c> with <c>MOVEFILE_REPLACE_EXISTING</c>). Encryption is keyed by the document id, so the
    ///     caller must pass the id that owns the target path — on the dedupe-repair path, the existing row's id.
    /// </remarks>
    private async Task WriteEncryptedBlobAsync(Guid documentId, string extension, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DocumentsDirectory());
        var bytesPath = BytesPath(documentId, extension);
        var encryptedBytes = _blobProtector.Encrypt(Guid.Empty, documentId, UploadedFileBlobProtector.FileBytesColumn, content.Span);
        var tempPath = string.Concat(bytesPath, ".", Guid.NewGuid().ToString("N"), TempSuffix);
        try
        {
            await File.WriteAllBytesAsync(tempPath, encryptedBytes, cancellationToken);
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
        await OpenIfNeededAsync(connection, cancellationToken);

        var extension = await SelectExtensionAsync(connection, documentId, cancellationToken);
        if (extension is null)
        {
            return null;
        }

        var bytesPath = BytesPath(documentId, extension);
        if (!File.Exists(bytesPath))
        {
            return null;
        }

        var encrypted = await File.ReadAllBytesAsync(bytesPath, cancellationToken);
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

    public IReadOnlyList<Guid> ListStoredDocumentIds()
    {
        var documentsDirectory = DocumentsDirectory();
        if (!Directory.Exists(documentsDirectory))
        {
            return [];
        }

        // File leaves are the document id plus its extension; ignore any stray file whose leading name is not a valid id
        // — a foreign file, or the temp/backup siblings of an interrupted write, reclaimed with their document below.
        var ids = new HashSet<Guid>();
        foreach (var file in Directory.EnumerateFiles(documentsDirectory))
        {
            if (Guid.TryParse(Path.GetFileNameWithoutExtension(file.AsSpan()), out var documentId))
            {
                _ = ids.Add(documentId);
            }
        }

        return [.. ids];
    }

    public KnowledgeBlobReconciliationResult ReconcileInterruptedWrites()
    {
        var documentsDirectory = DocumentsDirectory();
        if (!Directory.Exists(documentsDirectory))
        {
            return new KnowledgeBlobReconciliationResult { RestoredBlobNames = [], RemovedLitterCount = 0 };
        }

        var staleBefore = (_timeProvider.GetUtcNow() - InterruptedWriteGrace).UtcDateTime;
        var restored = new List<string>();
        var removed = 0;
        foreach (var path in Directory.GetFiles(documentsDirectory))
        {
            if (InterruptedWriteSuffix(path) is not { } suffix || LiveBlobPathOf(path, suffix) is not { } liveBlobPath)
            {
                continue;
            }

            // Recovery before reclamation: while the live path is missing, a backup IS the document its row still
            // claims to own, whatever its age, and deleting on age alone would be permanent loss of a live document.
            if (string.Equals(suffix, BackupSuffix, StringComparison.Ordinal) && !File.Exists(liveBlobPath))
            {
                if (TryRestoreBackup(path, liveBlobPath))
                {
                    restored.Add(Path.GetFileName(liveBlobPath));
                }

                continue;
            }

            // A temp is aged out, never promoted: its bytes were never verified, so installing them under a live row
            // would be worse than the missing blob AddAsync's dedupe-repair path already restores on the next re-add.
            if (File.GetLastWriteTimeUtc(path) < staleBefore && TryDeleteFile(path))
            {
                removed++;
            }
        }

        return new KnowledgeBlobReconciliationResult { RestoredBlobNames = restored, RemovedLitterCount = removed };
    }

    public Task DeleteAllBytesAsync(Guid documentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var documentsDirectory = DocumentsDirectory();
        if (!Directory.Exists(documentsDirectory))
        {
            return Task.CompletedTask;
        }

        // The extension died with the row, so match every file written under the id: the blob plus any temp/backup sibling
        // of an interrupted write. The id is server-generated, so no caller wildcard; materialized before deleting.
        foreach (var file in Directory.GetFiles(documentsDirectory, string.Concat(documentId.ToString("D"), "*")))
        {
            DeleteFileIfExists(file);
        }

        return Task.CompletedTask;
    }

    private async Task UpdateRepositoryDocumentAsync(DbConnection connection,
        NodeChatDbContext dbContext,
        DocumentIdentity row,
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
        var backupBlobPath = string.Concat(oldBlobPath, ".", Guid.NewGuid().ToString("N"), BackupSuffix);
        var backedUpOldBlob = false;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            // A reindex, not a delete: the document row stays, so no cascade applies and these projections must go by
            // hand before the row is marked Pending. Chunks come after vectors because deleting them fires the FTS trigger.
            await using (var vectorsCommand = connection.CreateCommand())
            {
                vectorsCommand.Transaction = transaction;
                vectorsCommand.CommandText = "DELETE FROM knowledge_chunk_vectors WHERE document_id = $document_id;";
                AddParameter(vectorsCommand, "$document_id", row.DocumentId);
                _ = await vectorsCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var chunksCommand = connection.CreateCommand())
            {
                chunksCommand.Transaction = transaction;
                chunksCommand.CommandText = "DELETE FROM knowledge_document_chunks WHERE document_id = $document_id;";
                AddParameter(chunksCommand, "$document_id", row.DocumentId);
                _ = await chunksCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var sectionsCommand = connection.CreateCommand())
            {
                sectionsCommand.Transaction = transaction;
                sectionsCommand.CommandText = "DELETE FROM knowledge_document_sections WHERE document_id = $document_id;";
                AddParameter(sectionsCommand, "$document_id", row.DocumentId);
                _ = await sectionsCommand.ExecuteNonQueryAsync(cancellationToken);
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
                _ = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            // The replacement blob is published to its live path BEFORE the commit, and that order is load-bearing for
            // KnowledgeBlobOrphanSweeper: it is what makes a surviving temp sibling provably not the row's content.
            if (string.Equals(oldBlobPath, replacementBlobPath, StringComparison.Ordinal) && File.Exists(oldBlobPath))
            {
                File.Move(oldBlobPath, backupBlobPath);
                backedUpOldBlob = true;
            }

            await WriteEncryptedBlobAsync(row.DocumentId, extension, input.Content, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            DeleteFileIfExists(backupBlobPath);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            DeleteFileIfExists(replacementBlobPath);
            if (backedUpOldBlob && File.Exists(backupBlobPath))
            {
                File.Move(backupBlobPath, oldBlobPath, overwrite: true);
            }

            throw;
        }

        if (!string.Equals(row.Extension, extension, StringComparison.Ordinal))
        {
            DeleteFileIfExists(oldBlobPath);
        }
    }

    private static async Task<DocumentIdentity?> SelectDocumentByIdentityAsync(DbConnection connection,
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

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var documentId = await reader.IsDBNullAsync(0, cancellationToken)
            ? Guid.Empty
            : Guid.Parse(reader.GetString(0));
        var extension = await reader.IsDBNullAsync(1, cancellationToken)
            ? string.Empty
            : reader.GetString(1);
        var storedContentHash = await reader.IsDBNullAsync(2, cancellationToken)
            ? string.Empty
            : reader.GetString(2);
        return new DocumentIdentity { DocumentId = documentId, Extension = extension, ContentHash = storedContentHash };
    }

    private static async Task<string?> SelectExtensionAsync(DbConnection connection, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT extension FROM knowledge_documents WHERE document_id = $document_id;";
        AddParameter(command, "$document_id", documentId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : result as string ?? string.Empty;
    }

    private static async Task DeleteRowAsync(DbConnection connection, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM knowledge_documents WHERE document_id = $document_id;";
        AddParameter(command, "$document_id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     Resets a repaired dedupe target back to Pending so the upload endpoint re-enqueues it for indexing, clearing
    ///     the stale content-missing failure and any partial chunk count.
    /// </summary>
    /// <remarks>
    ///     Called only after the missing blob has been restored from byte-identical content. The endpoint enqueues only
    ///     freshly-inserted or Pending rows, so without this reset the repaired bytes would never be indexed and every
    ///     identical re-upload would keep returning the stuck Failed document.
    /// </remarks>
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
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string DocumentsDirectory()
    {
        return Path.Combine(_dataDirectory.Root, RootFolderName, DocumentsFolderName);
    }

    private string BytesPath(Guid documentId, string extension)
    {
        return Path.Combine(DocumentsDirectory(), string.Concat(documentId.ToString("D"), extension));
    }

    /// <summary>The interrupted-write suffix this file carries, or null when it is not one this store wrote.</summary>
    private static string? InterruptedWriteSuffix(string path)
    {
        if (path.EndsWith(TempSuffix, StringComparison.Ordinal))
        {
            return TempSuffix;
        }

        return path.EndsWith(BackupSuffix, StringComparison.Ordinal) ? BackupSuffix : null;
    }

    /// <summary>The blob path a <c>{blobPath}.{guid:N}{suffix}</c> sibling belongs to, or null when it is foreign.</summary>
    /// <remarks>
    ///     The suffix and the write's own guid are stripped back off, and the remainder must still name a document the
    ///     way <see cref="ListStoredDocumentIds" /> requires — so a file this store did not write is never a candidate,
    ///     the same guarantee the id-keyed sweep gives.
    /// </remarks>
    private static string? LiveBlobPathOf(string path, string suffix)
    {
        var withoutSuffix = path[..^suffix.Length];
        var separator = withoutSuffix.LastIndexOf(value: '.');
        if (separator <= 0 || !Guid.TryParseExact(withoutSuffix[(separator + 1)..], "N", out _))
        {
            return null;
        }

        var liveBlobPath = withoutSuffix[..separator];
        return Guid.TryParse(Path.GetFileNameWithoutExtension(liveBlobPath.AsSpan()), out _) ? liveBlobPath : null;
    }

    private static bool TryRestoreBackup(string backupPath, string liveBlobPath)
    {
        try
        {
            File.Move(backupPath, liveBlobPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another writer won the live path, or the file is locked: leave the backup for the next start to retry.
            return false;
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        _ = TryDeleteFile(path);
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a transient IO error leaves the file behind. For a purged document's blob,
            // KnowledgeBlobOrphanSweeper reclaims it on the next start — a repeat purge never would, its row being gone.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; a permission error leaves the file behind, reclaimed by the same startup sweep.
        }

        return false;
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

    // An already-stored document matched by identity: its id plus the extension and content hash the row currently
    // carries — the extension locates the existing blob, the hash decides whether a repository re-add is an update.
    private sealed record DocumentIdentity
    {
        public required Guid DocumentId { get; init; }

        public required string Extension { get; init; }

        public required string ContentHash { get; init; }
    }
}
