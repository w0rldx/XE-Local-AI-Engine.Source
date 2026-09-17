namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     File-backed <see cref="IExternalAppCatalogCacheStore" />: a single small JSON file under the node data
///     directory, guarded by a lock and written owner-only on non-Windows. Copied from <c>ModelCatalogCacheStore</c>
///     in shape and intent; the catalog cache is a raw fetched document, not node state, so it stays a file rather
///     than a database row.
/// </summary>
internal sealed class ExternalAppCatalogCacheStore : IExternalAppCatalogCacheStore, IDisposable
{
    private const string CacheDirectoryName = "external-apps";
    private const string CacheFileName = "catalog-remote-cache.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _cachePath;
    private readonly SemaphoreSlim _lock = new(initialCount: 1, maxCount: 1);
    private readonly ILogger<ExternalAppCatalogCacheStore> _logger;

    public ExternalAppCatalogCacheStore(INodeDataDirectory dataDirectory, ILogger<ExternalAppCatalogCacheStore> logger)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cachePath = Path.Combine(dataDirectory.Root, CacheDirectoryName, CacheFileName);
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    public async Task<StoredExternalAppCatalogCache?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_cachePath))
            {
                return null;
            }

            try
            {
                await using var fileStream = File.OpenRead(_cachePath);
                return await JsonSerializer.DeserializeAsync<StoredExternalAppCatalogCache>(fileStream, SerializerOptions, cancellationToken);
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "Persisted External Apps catalog cache could not be deserialized; ignoring.");
                return null;
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Persisted External Apps catalog cache could not be read; ignoring.");
                return null;
            }
            catch (UnauthorizedAccessException exception)
            {
                // File.OpenRead throws this, not IOException, when the file's mode or ACL denies the node process.
                // A cache the node cannot read must degrade to the bundled catalog, never fail GetCatalogAsync.
                _logger.LogWarning(exception, "Persisted External Apps catalog cache could not be read; ignoring.");
                return null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(StoredExternalAppCatalogCache cache, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Unlike the model catalog's cache the path carries a subdirectory, so it may not exist yet on a node that
            // has never installed an external app.
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);

            await using var fileStream = CreateOwnerOnly(_cachePath);
            await JsonSerializer.SerializeAsync(fileStream, cache, SerializerOptions, cancellationToken);
        }
        catch (IOException exception)
        {
            // Persistence is best-effort: the in-memory refresh already took effect, so a write failure only means the
            // NEXT restart will not see this remote catalog — never fail the refresh itself over it.
            _logger.LogWarning(exception, "Persisted External Apps catalog cache could not be written; the in-memory refresh still applies.");
        }
        catch (UnauthorizedAccessException exception)
        {
            // Directory.CreateDirectory and the FileStream constructor both throw this rather than IOException when
            // the data directory denies the node process; it is the same best-effort outcome.
            _logger.LogWarning(exception, "Persisted External Apps catalog cache could not be written; the in-memory refresh still applies.");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     Opens a truncating write stream for <paramref name="path" />, created with owner-only (0600) permissions
    ///     atomically on non-Windows (mirrors <c>ModelCatalogCacheStore.CreateOwnerOnly</c>); Windows relies on the
    ///     per-user data-directory ACL.
    /// </summary>
    private static FileStream CreateOwnerOnly(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }
}
