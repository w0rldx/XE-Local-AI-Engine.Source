namespace XE_Local_AI_Engine.Client.Services.Images;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Providers.Abstractions;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Durable encrypted-at-rest <see cref="IGeneratedImageStore" />. The image bytes are encrypted on disk by
///     <see cref="ImageBlobProtector" /> under <c>INodeDataDirectory.Root/generated-images/{jobId}/{imageId}.png</c>; the
///     <c>generated_images</c> metadata row is written/read over the raw-SQL path (matching the uploaded-file store — the
///     row carries no encrypted column). Singleton: it opens a fresh DbContext scope per operation and depends only on
///     singletons (data directory, sqlite key holder, time provider).
/// </summary>
public sealed class GeneratedImageStore : IGeneratedImageStore
{
    private const string RootFolderName = "generated-images";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INodeDataDirectory _dataDirectory;
    private readonly ImageBlobProtector _blobProtector;
    private readonly TimeProvider _timeProvider;

    public GeneratedImageStore(IServiceScopeFactory scopeFactory,
        INodeDataDirectory dataDirectory,
        INodeSqliteKeyHolder keyHolder,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        ArgumentNullException.ThrowIfNull(keyHolder);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _blobProtector = new ImageBlobProtector(keyHolder);
    }

    public async Task<GeneratedImageInfo> AddAsync(Guid jobId,
        Guid imageId,
        ReadOnlyMemory<byte> pngBytes,
        GeneratedImageMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var jobDirectory = JobDirectory(jobId);
        Directory.CreateDirectory(jobDirectory);

        var bytesPath = BytesPath(jobDirectory, imageId);
        var encrypted = _blobProtector.Encrypt(jobId, imageId, ImageBlobProtector.ImageBytesColumn, pngBytes.Span);
        await File.WriteAllBytesAsync(bytesPath, encrypted, cancellationToken).ConfigureAwait(false);

        var createdAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var sizeBytes = (long)encrypted.Length;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
                              INSERT INTO generated_images (image_id, job_id, mime_type, width, height, size_bytes, storage_path, created_at_utc)
                              VALUES ($image_id, $job_id, $mime_type, $width, $height, $size_bytes, $storage_path, $created_at_utc);
                              """;
        AddParameter(command, "$image_id", imageId);
        AddParameter(command, "$job_id", jobId);
        AddParameter(command, "$mime_type", metadata.MimeType);
        AddParameter(command, "$width", metadata.Width);
        AddParameter(command, "$height", metadata.Height);
        AddParameter(command, "$size_bytes", sizeBytes);
        AddParameter(command, "$storage_path", bytesPath);
        AddParameter(command, "$created_at_utc", createdAtUtc);
        await OpenIfNeededAsync(command.Connection, cancellationToken).ConfigureAwait(false);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new GeneratedImageInfo(imageId, jobId, metadata.MimeType, metadata.Width, metadata.Height, sizeBytes, createdAtUtc);
    }

    public async Task<GeneratedImageContent?> OpenReadAsync(Guid imageId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
                              SELECT job_id, mime_type, width, height, storage_path
                              FROM generated_images
                              WHERE image_id = $image_id;
                              """;
        AddParameter(command, "$image_id", imageId);
        await OpenIfNeededAsync(command.Connection, cancellationToken).ConfigureAwait(false);

        Guid jobId;
        string mimeType;
        int width;
        int height;
        string storagePath;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            jobId = Guid.Parse(reader.GetString(0));
            mimeType = reader.GetString(1);
            width = reader.GetInt32(2);
            height = reader.GetInt32(3);
            storagePath = reader.GetString(4);
        }

        if (!File.Exists(storagePath))
        {
            return null;
        }

        var encrypted = await File.ReadAllBytesAsync(storagePath, cancellationToken).ConfigureAwait(false);
        var plaintext = _blobProtector.Decrypt(jobId, imageId, ImageBlobProtector.ImageBytesColumn, encrypted);
        return new GeneratedImageContent(plaintext, mimeType, width, height);
    }

    private string JobDirectory(Guid jobId)
    {
        return Path.Combine(_dataDirectory.Root, RootFolderName, jobId.ToString("D"));
    }

    private static string BytesPath(string jobDirectory, Guid imageId)
    {
        return Path.Combine(jobDirectory, string.Concat(imageId.ToString("D"), ".png"));
    }
}
