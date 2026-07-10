namespace XE_Local_AI_Engine.Client.Services.ModelFit.Catalog.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     File-backed <see cref="IModelCatalogCacheStore" />: a single small JSON file in the node data directory, guarded
///     by a lock and written owner-only on non-Windows. Mirrors <c>NodeSettingsStore</c>'s persistence shape but keeps
///     the remote catalog cache in its own file — this is a raw fetched document, not a settings key.
/// </summary>
internal sealed class ModelCatalogCacheStore : IModelCatalogCacheStore, IDisposable
{
    private const string CacheFileName = "model-catalog-remote-cache.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _cachePath;
    private readonly SemaphoreSlim _lock = new(initialCount: 1, maxCount: 1);
    private readonly ILogger<ModelCatalogCacheStore> _logger;

    public ModelCatalogCacheStore(INodeDataDirectory dataDirectory, ILogger<ModelCatalogCacheStore> logger)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cachePath = Path.Combine(dataDirectory.Root, CacheFileName);
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    public async Task<StoredModelCatalogCache?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_cachePath))
            {
                return null;
            }

            try
            {
                await using var fileStream = File.OpenRead(_cachePath);
                return await JsonSerializer.DeserializeAsync<StoredModelCatalogCache>(fileStream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "Persisted model catalog cache could not be deserialized; ignoring.");
                return null;
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Persisted model catalog cache could not be read; ignoring.");
                return null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(StoredModelCatalogCache cache, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fileStream = CreateOwnerOnly(_cachePath);
            await JsonSerializer.SerializeAsync(fileStream, cache, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            // Persistence is best-effort: the in-memory refresh already took effect, so a write failure only means the
            // NEXT restart will not see this remote catalog — never fail the refresh itself over it.
            _logger.LogWarning(exception, "Persisted model catalog cache could not be written; the in-memory refresh still applies.");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     Opens a truncating write stream for <paramref name="path" />, created with owner-only (0600) permissions
    ///     atomically on non-Windows (mirrors <c>NodeSettingsStore.CreateOwnerOnly</c>); Windows relies on the per-user
    ///     data-directory ACL.
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
