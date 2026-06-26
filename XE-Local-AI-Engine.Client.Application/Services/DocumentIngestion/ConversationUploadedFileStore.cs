namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Providers.Abstractions;
using static XE_Local_AI_Engine.Client.Services.Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Durable per-conversation uploaded-file store. Metadata rows are written/read over the raw-SQL path (matching the
///     node chat persistence path) with the display name encrypted via the matching <see cref="NodeChatDbContext" />
///     helper; the bytes and cached extracted Markdown are encrypted on disk by <see cref="UploadedFileBlobProtector" />
///     under <c>INodeDataDirectory.Root/uploaded-files/conversations/</c>. The store is a singleton and opens a fresh
///     scope per database operation (uploaded files have unique ids, so no per-conversation write serialization is
///     required).
/// </summary>
public sealed class ConversationUploadedFileStore : IConversationUploadedFileStore
{
    private const string RootFolderName = "uploaded-files";
    private const string ConversationsFolderName = "conversations";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INodeDataDirectory _dataDirectory;
    private readonly UploadedFileBlobProtector _blobProtector;
    private readonly TimeProvider _timeProvider;

    public ConversationUploadedFileStore(IServiceScopeFactory scopeFactory,
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

    public async Task<ConversationUploadedFileInfo> AddAsync(ConversationUploadedFileInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.OriginalFileName))
        {
            throw new ArgumentException("The uploaded file must have a display name.", nameof(input));
        }

        var extension = NormalizeExtension(input.Extension);
        var conversationDirectory = ConversationDirectory(input.ConversationId);
        Directory.CreateDirectory(conversationDirectory);

        var bytesPath = BytesPath(conversationDirectory, input.FileId, extension);
        var encryptedBytes = _blobProtector.Encrypt(input.ConversationId, input.FileId, UploadedFileBlobProtector.FileBytesColumn, input.Content.Span);
        await File.WriteAllBytesAsync(bytesPath, encryptedBytes, cancellationToken).ConfigureAwait(false);

        if (input.ExtractedMarkdown is not null)
        {
            var markdownPath = MarkdownPath(conversationDirectory, input.FileId);
            var encryptedMarkdown = _blobProtector.Encrypt(input.ConversationId, input.FileId, UploadedFileBlobProtector.FileMarkdownColumn, Encoding.UTF8.GetBytes(input.ExtractedMarkdown));
            await File.WriteAllBytesAsync(markdownPath, encryptedMarkdown, cancellationToken).ConfigureAwait(false);
        }

        var createdAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var storagePath = string.Concat(input.ConversationId.ToString("D"), "/", input.FileId.ToString("D"), extension);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        var encryptedName = dbContext.EncryptUploadedFileName(input.OriginalFileName, input.ConversationId, input.FileId);

        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
                              INSERT INTO conversation_uploaded_files (file_id, conversation_id, original_file_name, mime_type, extension, size_bytes, extraction_status, extracted_chars, storage_path, created_at_utc)
                              VALUES ($file_id, $conversation_id, $original_file_name, $mime_type, $extension, $size_bytes, $extraction_status, $extracted_chars, $storage_path, $created_at_utc);
                              """;
        AddParameter(command, "$file_id", input.FileId);
        AddParameter(command, "$conversation_id", input.ConversationId);
        AddParameter(command, "$original_file_name", encryptedName);
        AddParameter(command, "$mime_type", input.MimeType);
        AddParameter(command, "$extension", extension);
        AddParameter(command, "$size_bytes", input.SizeBytes);
        AddParameter(command, "$extraction_status", input.ExtractionStatus.ToString());
        AddParameter(command, "$extracted_chars", input.ExtractedChars);
        AddParameter(command, "$storage_path", storagePath);
        AddParameter(command, "$created_at_utc", createdAtUtc);
        await OpenIfNeededAsync(command.Connection, cancellationToken).ConfigureAwait(false);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new ConversationUploadedFileInfo(input.FileId,
            input.ConversationId,
            input.OriginalFileName,
            input.MimeType,
            extension,
            input.SizeBytes,
            input.ExtractionStatus,
            input.ExtractedChars,
            createdAtUtc);
    }

    public async Task<IReadOnlyList<ConversationUploadedFileInfo>> ListAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
                              SELECT file_id, conversation_id, original_file_name, mime_type, extension, size_bytes, extraction_status, extracted_chars, created_at_utc
                              FROM conversation_uploaded_files
                              WHERE conversation_id = $conversation_id
                              ORDER BY created_at_utc ASC, file_id ASC;
                              """;
        AddParameter(command, "$conversation_id", conversationId);
        await OpenIfNeededAsync(command.Connection, cancellationToken).ConfigureAwait(false);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var files = new List<ConversationUploadedFileInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var fileId = Guid.Parse(reader.GetString(0));
            var ownerConversationId = Guid.Parse(reader.GetString(1));
            var nameBytes = await reader.GetFieldValueAsync<byte[]>(ordinal: 2, cancellationToken).ConfigureAwait(false);
            var originalFileName = dbContext.DecryptUploadedFileName(nameBytes, ownerConversationId, fileId);

            files.Add(new ConversationUploadedFileInfo(fileId,
                ownerConversationId,
                originalFileName,
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                ParseStatus(reader.GetString(6)),
                await reader.IsDBNullAsync(ordinal: 7, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt32(7),
                reader.GetInt64(8)));
        }

        return files;
    }

    public async Task<string?> ReadExtractedMarkdownAsync(Guid conversationId, Guid fileId, CancellationToken cancellationToken)
    {
        var markdownPath = MarkdownPath(ConversationDirectory(conversationId), fileId);
        if (!File.Exists(markdownPath))
        {
            return null;
        }

        var encrypted = await File.ReadAllBytesAsync(markdownPath, cancellationToken).ConfigureAwait(false);
        var plaintext = _blobProtector.Decrypt(conversationId, fileId, UploadedFileBlobProtector.FileMarkdownColumn, encrypted);
        return Encoding.UTF8.GetString(plaintext);
    }

    public async Task<bool> DeleteAsync(Guid conversationId, Guid fileId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        // Read the stored extension so the server-named bytes file can be located precisely; a missing row means there
        // is nothing to delete.
        string extension;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = "SELECT extension FROM conversation_uploaded_files WHERE conversation_id = $conversation_id AND file_id = $file_id;";
            AddParameter(lookup, "$conversation_id", conversationId);
            AddParameter(lookup, "$file_id", fileId);
            var result = await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is null or DBNull)
            {
                return false;
            }

            extension = result as string ?? string.Empty;
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM conversation_uploaded_files WHERE conversation_id = $conversation_id AND file_id = $file_id;";
            AddParameter(delete, "$conversation_id", conversationId);
            AddParameter(delete, "$file_id", fileId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var conversationDirectory = ConversationDirectory(conversationId);
        DeleteFileIfExists(BytesPath(conversationDirectory, fileId, extension));
        DeleteFileIfExists(MarkdownPath(conversationDirectory, fileId));
        return true;
    }

    public Task DeleteAllForConversationAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Disk-only: the metadata rows are removed by the caller's conversation-delete path (the node-sqlite runtime
        // connection does not enforce the FK cascade), so this only tears down the on-disk upload directory.
        var conversationDirectory = ConversationDirectory(conversationId);
        try
        {
            if (Directory.Exists(conversationDirectory))
            {
                Directory.Delete(conversationDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort teardown; a transient IO error is not worth surfacing to the delete path.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort teardown; a permission error is not worth surfacing to the delete path.
        }

        return Task.CompletedTask;
    }

    public async Task<IConversationStagingSnapshot> CreateStagingSnapshotAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var files = await ListAsync(conversationId, cancellationToken).ConfigureAwait(false);
        var stagingDirectory = Directory.CreateTempSubdirectory("xe-attachments-").FullName;

        try
        {
            var fileCount = 0;
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var markdown = await ReadExtractedMarkdownAsync(conversationId, file.FileId, cancellationToken).ConfigureAwait(false);
                if (markdown is null)
                {
                    continue;
                }

                var stagedName = BuildStagedFileName(file, usedNames);
                await File.WriteAllTextAsync(Path.Combine(stagingDirectory, stagedName), markdown, cancellationToken).ConfigureAwait(false);
                fileCount++;
            }

            return new ConversationStagingSnapshot(stagingDirectory, fileCount);
        }
        catch
        {
            // Never leave a half-built plaintext directory behind on failure.
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    private string ConversationDirectory(Guid conversationId)
    {
        return Path.Combine(_dataDirectory.Root, RootFolderName, ConversationsFolderName, conversationId.ToString("D"));
    }

    private static string BytesPath(string conversationDirectory, Guid fileId, string extension)
    {
        return Path.Combine(conversationDirectory, string.Concat(fileId.ToString("D"), extension));
    }

    private static string MarkdownPath(string conversationDirectory, Guid fileId)
    {
        return Path.Combine(conversationDirectory, string.Concat(fileId.ToString("D"), ".md"));
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
            // Best-effort cleanup; a transient IO error leaves an orphan blob that the conversation teardown also covers.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; a permission error leaves an orphan blob that the conversation teardown also covers.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the transient staging directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of the transient staging directory.
        }
    }

    private static string BuildStagedFileName(ConversationUploadedFileInfo file, HashSet<string> usedNames)
    {
        // Stage under a friendly, sanitized leaf so the agent's read tools see recognizable names; fall back to the
        // opaque file id when the display name has no usable leaf, and de-duplicate collisions with a short id suffix.
        var leaf = SanitizeLeaf(Path.GetFileNameWithoutExtension(file.OriginalFileName));
        if (string.IsNullOrEmpty(leaf))
        {
            leaf = file.FileId.ToString("N");
        }

        var candidate = string.Concat(leaf, ".md");
        if (!usedNames.Add(candidate))
        {
            candidate = string.Concat(leaf, "-", file.FileId.ToString("N").AsSpan(start: 0, length: 8), ".md");
            _ = usedNames.Add(candidate);
        }

        return candidate;
    }

    private static string SanitizeLeaf(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        return builder.ToString().Trim();
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

    private static DocumentExtractionStatus ParseStatus(string status)
    {
        // Defensive: an unrecognized persisted value degrades to Failed rather than throwing on read.
        return Enum.TryParse<DocumentExtractionStatus>(status, ignoreCase: false, out var parsed) ? parsed : DocumentExtractionStatus.Failed;
    }
}
