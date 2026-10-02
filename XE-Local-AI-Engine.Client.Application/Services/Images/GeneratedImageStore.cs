namespace XE_Local_AI_Engine.Client.Services.Images;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>Durable encrypted-at-rest <see cref="IGeneratedImageStore" />.</summary>
/// <remarks>
///     The image bytes are encrypted on disk by <see cref="ImageBlobProtector" /> under
///     <c>INodeDataDirectory.Root/generated-images/{jobId}/{imageId}.png</c>; the <c>generated_images</c> metadata row
///     lives behind <see cref="IGeneratedImageRowStore" /> (no DbContext above the persistence
///     layer). Singleton: it opens a fresh scope per row operation and depends only on singletons (data directory,
///     sqlite key holder, time provider).
/// </remarks>
public sealed class GeneratedImageStore : IGeneratedImageStore
{
    private const string RootFolderName = "generated-images";
    private const string UploadsFolderName = "uploads";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INodeDataDirectory _dataDirectory;
    private readonly ImageBlobProtector _blobProtector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GeneratedImageStore> _logger;

    public GeneratedImageStore(IServiceScopeFactory scopeFactory,
        INodeDataDirectory dataDirectory,
        INodeSqliteKeyHolder keyHolder,
        TimeProvider timeProvider,
        ILogger<GeneratedImageStore> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        ArgumentNullException.ThrowIfNull(keyHolder);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        await File.WriteAllBytesAsync(bytesPath, encrypted, cancellationToken);

        var createdAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var sizeBytes = (long)encrypted.Length;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var rows = scope.ServiceProvider.GetRequiredService<IGeneratedImageRowStore>();

        await rows.InsertAsync(new GeneratedImageRow
            {
                ImageId = imageId,
                JobId = jobId,
                MimeType = metadata.MimeType,
                Width = metadata.Width,
                Height = metadata.Height,
                SizeBytes = sizeBytes,
                CreatedAtUtc = createdAtUtc
            },
            bytesPath,
            cancellationToken);

        return new GeneratedImageInfo
        {
            ImageId = imageId,
            JobId = jobId,
            MimeType = metadata.MimeType,
            Width = metadata.Width,
            Height = metadata.Height,
            SizeBytes = sizeBytes,
            CreatedAtUtc = createdAtUtc
        };
    }

    public async Task<GeneratedImageInfo> AddUploadAsync(ReadOnlyMemory<byte> bytes, GeneratedImageMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var imageId = Guid.NewGuid();
        var uploadsDirectory = Path.Combine(_dataDirectory.Root, RootFolderName, UploadsFolderName);
        Directory.CreateDirectory(uploadsDirectory);

        var extension = string.Equals(metadata.MimeType, "image/jpeg", StringComparison.Ordinal) ? ".jpg" : ".png";
        var bytesPath = Path.Combine(uploadsDirectory, string.Concat(imageId.ToString("D"), extension));

        // Guid.Empty stands in for the job in the associated data: an upload blob can never decrypt as a job's image, nor a job's as an upload.
        var encrypted = _blobProtector.Encrypt(Guid.Empty, imageId, ImageBlobProtector.ImageBytesColumn, bytes.Span);
        var createdAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var sizeBytes = (long)encrypted.Length;

        try
        {
            await File.WriteAllBytesAsync(bytesPath, encrypted, cancellationToken);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var rows = scope.ServiceProvider.GetRequiredService<IGeneratedImageRowStore>();
            await rows.InsertAsync(new GeneratedImageRow
                {
                    ImageId = imageId,
                    JobId = null,
                    MimeType = metadata.MimeType,
                    Width = metadata.Width,
                    Height = metadata.Height,
                    SizeBytes = sizeBytes,
                    CreatedAtUtc = createdAtUtc
                },
                bytesPath,
                cancellationToken);
        }
        catch
        {
            // An upload has no job directory and no sweep: a blob whose row never landed would be unreachable forever.
            DeleteOrphanedUploadBlob(imageId, bytesPath);
            throw;
        }

        return new GeneratedImageInfo
        {
            ImageId = imageId,
            JobId = null,
            MimeType = metadata.MimeType,
            Width = metadata.Width,
            Height = metadata.Height,
            SizeBytes = sizeBytes,
            CreatedAtUtc = createdAtUtc
        };
    }

    public async Task<GeneratedImageContent?> OpenReadAsync(Guid imageId, CancellationToken cancellationToken)
    {
        GeneratedImageLocation? location;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var rows = scope.ServiceProvider.GetRequiredService<IGeneratedImageRowStore>();
            location = await rows.FindAsync(imageId, cancellationToken);
        }

        if (location is null)
        {
            return null;
        }

        // Read only from under the blob root, the same rule the deletes enforce: a tampered storage_path must not turn the retrieve route into a file reader.
        var blobRoot = Path.GetFullPath(Path.Combine(_dataDirectory.Root, RootFolderName));
        if (!PathContainment.IsUnderRoot(location.StoragePath, blobRoot) || !File.Exists(location.StoragePath))
        {
            return null;
        }

        var encrypted = await File.ReadAllBytesAsync(location.StoragePath, cancellationToken);
        // An upload has no job; its blob was bound to Guid.Empty when it was written (AddUploadAsync).
        var plaintext = _blobProtector.Decrypt(location.JobId ?? Guid.Empty, imageId, ImageBlobProtector.ImageBytesColumn, encrypted);
        return new GeneratedImageContent
        {
            Bytes = plaintext,
            MimeType = location.MimeType,
            Width = location.Width,
            Height = location.Height
        };
    }

    public void RemoveJobBlobs(Guid jobId, IReadOnlyList<string> storagePaths)
    {
        ArgumentNullException.ThrowIfNull(storagePaths);

        // Every path is proved to resolve under the blob root before it is unlinked. The stored value is server-computed today (AddAsync builds it from two minted
        // Guids), but this is the deletion boundary: it enforces its own invariant rather than trusting a column, so a legacy, migrated or hand-edited row cannot make it delete.
        var blobRoot = Path.GetFullPath(Path.Combine(_dataDirectory.Root, RootFolderName));

        // The recorded storage_path is unlinked rather than a path recomputed from the current data directory: the row is what says where the bytes actually
        // landed, and a node whose data directory moved would otherwise leave every older blob behind.
        foreach (var storagePath in storagePaths)
        {
            if (!PathContainment.IsUnderRoot(storagePath, blobRoot))
            {
                // The path itself is never logged (privacy §10 — no path leaves this feature), so the warning names
                // the job and the refusal only.
                _logger.LogWarning("An image blob of deleted job {JobId} resolves outside the image blob root; it was left untouched.", jobId);
                continue;
            }

            try
            {
                File.Delete(storagePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not delete the image blob of deleted job {JobId}; the file is left orphaned.", jobId);
            }
        }

        var jobDirectory = JobDirectory(jobId);
        if (!PathContainment.IsUnderRoot(jobDirectory, blobRoot))
        {
            // Unreachable while the data directory is a normal absolute path, but the guard is on the delete, not on
            // the caller: the same rule that protects a blob protects the directory it sat in.
            _logger.LogWarning("The image directory of deleted job {JobId} resolves outside the image blob root; it was left untouched.", jobId);
            return;
        }

        try
        {
            // Non-recursive on purpose: it removes the directory only once it is empty, so a blob that survived the
            // loop above (or one this job never knew about) is never taken out with it.
            Directory.Delete(jobDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(exception, "Could not remove the image directory of deleted job {JobId}.", jobId);
        }
    }

    public void RemoveUploadBlob(Guid imageId, string storagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        var uploadsRoot = Path.GetFullPath(Path.Combine(_dataDirectory.Root, RootFolderName, UploadsFolderName));
        if (!PathContainment.IsUnderRoot(storagePath, uploadsRoot))
        {
            // The path is never logged (privacy), only the refusal.
            _logger.LogWarning("The blob of deleted uploaded image {ImageId} resolves outside the upload blob root; it was left untouched.", imageId);
            return;
        }

        try
        {
            File.Delete(storagePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not delete the blob of deleted uploaded image {ImageId}; the file is left orphaned.", imageId);
        }
    }

    private void DeleteOrphanedUploadBlob(Guid imageId, string bytesPath)
    {
        try
        {
            File.Delete(bytesPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The path is never logged (privacy), only the image id.
            _logger.LogWarning(exception, "Could not delete the blob of uploaded image {ImageId} whose row was never written; the file is left orphaned.", imageId);
            return;
        }

        _logger.LogWarning("The upload of image {ImageId} failed after its blob was written; the blob was removed.", imageId);
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
