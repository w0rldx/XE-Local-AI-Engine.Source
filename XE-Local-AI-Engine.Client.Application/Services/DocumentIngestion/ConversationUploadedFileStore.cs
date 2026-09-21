namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

using System.Diagnostics.CodeAnalysis;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>Durable per-conversation uploaded-file store.</summary>
/// <remarks>
///     The metadata rows live behind <see cref="IConversationUploadedFileRowStore" />, which also encrypts the display
///     name. What stays here is what the database does not hold: the bytes and cached Markdown, encrypted on disk by
///     <see cref="UploadedFileBlobProtector" />, and the plaintext staging snapshot the agent sandbox copies from.
///     Singleton, opening a fresh scope per row operation — uploaded files have unique ids, so no per-conversation
///     write serialization is required.
/// </remarks>
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
        await File.WriteAllBytesAsync(bytesPath, encryptedBytes, cancellationToken);

        if (input.ExtractedMarkdown is not null)
        {
            var markdownPath = MarkdownPath(conversationDirectory, input.FileId);
            var encryptedMarkdown = _blobProtector.Encrypt(input.ConversationId, input.FileId, UploadedFileBlobProtector.FileMarkdownColumn, Encoding.UTF8.GetBytes(input.ExtractedMarkdown));
            await File.WriteAllBytesAsync(markdownPath, encryptedMarkdown, cancellationToken);
        }

        var createdAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var storagePath = string.Concat(input.ConversationId.ToString("D"), "/", input.FileId.ToString("D"), extension);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var rows = scope.ServiceProvider.GetRequiredService<IConversationUploadedFileRowStore>();

        await rows.InsertAsync(new ConversationUploadedFileRow
            {
                FileId = input.FileId,
                ConversationId = input.ConversationId,
                OriginalFileName = input.OriginalFileName,
                MimeType = input.MimeType,
                Extension = extension,
                SizeBytes = input.SizeBytes,
                ExtractionStatus = input.ExtractionStatus.ToString(),
                ExtractedChars = input.ExtractedChars,
                CreatedAtUtc = createdAtUtc
            },
            storagePath,
            cancellationToken);

        return new ConversationUploadedFileInfo
        {
            FileId = input.FileId,
            ConversationId = input.ConversationId,
            OriginalFileName = input.OriginalFileName,
            MimeType = input.MimeType,
            Extension = extension,
            SizeBytes = input.SizeBytes,
            ExtractionStatus = input.ExtractionStatus,
            ExtractedChars = input.ExtractedChars,
            CreatedAtUtc = createdAtUtc
        };
    }

    public async Task<IReadOnlyList<ConversationUploadedFileInfo>> ListAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var rows = scope.ServiceProvider.GetRequiredService<IConversationUploadedFileRowStore>();

        var files = await rows.ListAsync(conversationId, cancellationToken);
        return files.Select(static row => new ConversationUploadedFileInfo
                     {
                         FileId = row.FileId,
                         ConversationId = row.ConversationId,
                         OriginalFileName = row.OriginalFileName,
                         MimeType = row.MimeType,
                         Extension = row.Extension,
                         SizeBytes = row.SizeBytes,
                         ExtractionStatus = ParseStatus(row.ExtractionStatus),
                         ExtractedChars = row.ExtractedChars,
                         CreatedAtUtc = row.CreatedAtUtc
                     })
                    .ToArray();
    }

    public async Task<string?> ReadExtractedMarkdownAsync(Guid conversationId, Guid fileId, CancellationToken cancellationToken)
    {
        var markdownPath = MarkdownPath(ConversationDirectory(conversationId), fileId);
        if (!File.Exists(markdownPath))
        {
            return null;
        }

        var encrypted = await File.ReadAllBytesAsync(markdownPath, cancellationToken);
        var plaintext = _blobProtector.Decrypt(conversationId, fileId, UploadedFileBlobProtector.FileMarkdownColumn, encrypted);
        return Encoding.UTF8.GetString(plaintext);
    }

    public async Task<ReadOnlyMemory<byte>?> ReadBytesAsync(Guid conversationId, Guid fileId, CancellationToken cancellationToken)
    {
        // The bytes blob is server-named from the file id plus its extension, which is not passed in here, so locate it by
        // the unique file-id prefix, excluding the ".md" companion. No DB round-trip on the send hot path.
        var bytesPath = FindBytesFilePath(ConversationDirectory(conversationId), fileId);
        if (bytesPath is null)
        {
            return null;
        }

        var encrypted = await File.ReadAllBytesAsync(bytesPath, cancellationToken);
        ReadOnlyMemory<byte> plaintext = _blobProtector.Decrypt(conversationId, fileId, UploadedFileBlobProtector.FileBytesColumn, encrypted);
        return plaintext;
    }

    public async Task<bool> DeleteAsync(Guid conversationId, Guid fileId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var rows = scope.ServiceProvider.GetRequiredService<IConversationUploadedFileRowStore>();

        // The store returns the stored extension so the server-named bytes file can be located precisely; null means
        // there was no row, and therefore nothing to delete.
        if (await rows.DeleteAsync(conversationId, fileId, cancellationToken) is not { } extension)
        {
            return false;
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

    public IReadOnlyList<Guid> ListConversationDirectoryIds()
    {
        var conversationsRoot = Path.Combine(_dataDirectory.Root, RootFolderName, ConversationsFolderName);
        if (!Directory.Exists(conversationsRoot))
        {
            return [];
        }

        var ids = new List<Guid>();
        foreach (var directory in Directory.EnumerateDirectories(conversationsRoot))
        {
            // Directory leaves are conversation ids ("D" form). Ignore any stray directory that is not a valid id.
            if (Guid.TryParse(Path.GetFileName(directory), out var conversationId))
            {
                ids.Add(conversationId);
            }
        }

        return ids;
    }

    public async Task<IConversationStagingSnapshot> CreateStagingSnapshotAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var files = await ListAsync(conversationId, cancellationToken);
        var stagingDirectory = Directory.CreateTempSubdirectory("xe-attachments-").FullName;

        try
        {
            var stagedNames = new List<string>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var markdown = await ReadExtractedMarkdownAsync(conversationId, file.FileId, cancellationToken);
                if (markdown is null)
                {
                    continue;
                }

                var stagedName = BuildStagedFileName(file, usedNames);
                await File.WriteAllTextAsync(Path.Combine(stagingDirectory, stagedName), markdown, cancellationToken);
                stagedNames.Add(stagedName);
            }

            return new ConversationStagingSnapshot(stagingDirectory, stagedNames);
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

    // Locates the on-disk bytes blob for a file by its unique file-id name, excluding the ".md" companion; null when the
    // directory, the blob, or its extension is absent — images always carry one, so only a degenerate upload is skipped.
    private static string? FindBytesFilePath(string conversationDirectory, Guid fileId)
    {
        if (!Directory.Exists(conversationDirectory))
        {
            return null;
        }

        var markdownPath = MarkdownPath(conversationDirectory, fileId);
        return Directory.EnumerateFiles(conversationDirectory, string.Concat(fileId.ToString("D"), ".*"))
                        .FirstOrDefault(path => !string.Equals(path, markdownPath, StringComparison.Ordinal));
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
