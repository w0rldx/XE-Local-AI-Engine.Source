namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Diagnostics.CodeAnalysis;
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
        var storagePath = string.Concat(input.DocumentId.ToString("D"), extension);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        var encryptedName = dbContext.EncryptKnowledgeFileName(input.OriginalFileName, input.DocumentId);

        // Content-hash dedupe: never check-then-insert. Insert with ON CONFLICT DO NOTHING, then re-select the existing
        // id when a concurrent identical upload already won the race.
        int inserted;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                                  INSERT INTO knowledge_documents (document_id, original_file_name, mime_type, extension, size_bytes, content_hash, storage_path, status, failure_reason, chunk_count, embedding_model, created_at_utc, updated_at_utc)
                                  VALUES ($document_id, $original_file_name, $mime_type, $extension, $size_bytes, $content_hash, $storage_path, $status, $failure_reason, $chunk_count, $embedding_model, $created_at_utc, $updated_at_utc)
                                  ON CONFLICT(content_hash) DO NOTHING;
                                  """;
            AddParameter(command, "$document_id", input.DocumentId);
            AddParameter(command, "$original_file_name", encryptedName);
            AddParameter(command, "$mime_type", input.MimeType);
            AddParameter(command, "$extension", extension);
            AddParameter(command, "$size_bytes", input.SizeBytes);
            AddParameter(command, "$content_hash", input.ContentHash);
            AddParameter(command, "$storage_path", storagePath);
            AddParameter(command, "$status", KnowledgeDocumentStatus.Pending.ToString());
            AddParameter(command, "$failure_reason", value: null);
            AddParameter(command, "$chunk_count", value: 0);
            AddParameter(command, "$embedding_model", input.EmbeddingModel);
            AddParameter(command, "$created_at_utc", now);
            AddParameter(command, "$updated_at_utc", now);
            inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (inserted == 0)
        {
            // Dedupe hit: the identical content already exists under a different id. Do not write a second blob.
            var existingId = await SelectDocumentIdByHashAsync(connection, input.ContentHash, cancellationToken).ConfigureAwait(false);
            return new KnowledgeDocumentAddResult(existingId, WasInserted: false);
        }

        // Only write the encrypted blob for a freshly inserted row so a dedupe never orphans bytes on disk. If the blob
        // write fails, roll the row back so we never leave a document row without its bytes.
        try
        {
            Directory.CreateDirectory(DocumentsDirectory());
            var bytesPath = BytesPath(input.DocumentId, extension);
            var encryptedBytes = _blobProtector.Encrypt(Guid.Empty, input.DocumentId, UploadedFileBlobProtector.FileBytesColumn, input.Content.Span);
            await File.WriteAllBytesAsync(bytesPath, encryptedBytes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DeleteRowAsync(connection, input.DocumentId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new KnowledgeDocumentAddResult(input.DocumentId, WasInserted: true);
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

    private static async Task<Guid> SelectDocumentIdByHashAsync(System.Data.Common.DbConnection connection, string contentHash, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document_id FROM knowledge_documents WHERE content_hash = $content_hash;";
        AddParameter(command, "$content_hash", contentHash);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string id ? Guid.Parse(id) : Guid.Empty;
    }

    private static async Task<string?> SelectExtensionAsync(System.Data.Common.DbConnection connection, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT extension FROM knowledge_documents WHERE document_id = $document_id;";
        AddParameter(command, "$document_id", documentId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : result as string ?? string.Empty;
    }

    private static async Task DeleteRowAsync(System.Data.Common.DbConnection connection, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM knowledge_documents WHERE document_id = $document_id;";
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
}
