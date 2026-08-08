namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

/// <summary>
///     File-based <see cref="IImageModelRegistry" /> backed by a JSON manifest (<c>image-models.json</c>) under the
///     image-models directory. The single owner of the manifest (read + write under a semaphore);
///     <see cref="HuggingFaceImageModelStore" /> calls the internal write methods, while the public interface stays
///     read-only. Mirrors <see cref="GgufModelRegistry" /> but each entry is a diffusion-model file-<b>set</b>.
/// </summary>
internal sealed class ImageModelRegistry : IImageModelRegistry, IDisposable
{
    private const string ManifestFileName = "image-models.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    private readonly SemaphoreSlim _lock = new(initialCount: 1, maxCount: 1);
    private readonly ILogger<ImageModelRegistry> _logger;
    private readonly string _manifestPath;
    private readonly string _modelsDirectory;

    public ImageModelRegistry(ImageModelStoreOptions options, ILogger<ImageModelRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _modelsDirectory = options.ModelsDirectory;
        _manifestPath = Path.Combine(_modelsDirectory, ManifestFileName);
        _logger = logger;
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImageModelRegistryEntry>> ListAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LoadEntriesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ImageModelRegistryEntry?> FindAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await LoadEntriesAsync(ct).ConfigureAwait(false);
            return entries.FirstOrDefault(entry => string.Equals(entry.ModelName, modelName, StringComparison.Ordinal));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Inserts or replaces the entry keyed by <see cref="ImageModelRegistryEntry.ModelName" />.</summary>
    public async Task UpsertAsync(ImageModelRegistryEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = (await LoadEntriesAsync(ct).ConfigureAwait(false)).ToList();
            entries.RemoveAll(existing => string.Equals(existing.ModelName, entry.ModelName, StringComparison.Ordinal));
            entries.Add(entry);
            await WriteManifestAsync(entries, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Removes the entry keyed by <paramref name="modelName" />. Idempotent — absent is a no-op.</summary>
    public async Task RemoveAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = (await LoadEntriesAsync(ct).ConfigureAwait(false)).ToList();
            var removed = entries.RemoveAll(existing => string.Equals(existing.ModelName, modelName, StringComparison.Ordinal));
            if (removed > 0)
            {
                await WriteManifestAsync(entries, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    // Reads the manifest, self-healing to an empty set when it is missing or corrupt. A file-set cannot be reconstructed
    // from loose files on disk (family/part-role are not derivable), so — unlike the GGUF registry's directory rescan —
    // a lost manifest yields no entries and the store re-materializes them on the next ensure. Caller holds the lock.
    private async Task<IReadOnlyList<ImageModelRegistryEntry>> LoadEntriesAsync(CancellationToken ct)
    {
        if (!File.Exists(_manifestPath))
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(_manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var manifest = await JsonSerializer
                                 .DeserializeAsync<ManifestDocument>(stream, SerializerOptions, ct)
                                 .ConfigureAwait(false);

            if (manifest?.Models is null)
            {
                return [];
            }

            // Drop any entry whose backing part files disappeared since the manifest was written (manual deletion).
            return manifest.Models
                           .Where(static entry => entry.Parts.Count > 0 && entry.Parts.All(part => File.Exists(part.LocalPath)))
                           .ToList();
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Image model registry manifest is corrupt. Treating the image-model set as empty until the next download.");
            return [];
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Image model registry manifest could not be read. Treating the image-model set as empty until the next download.");
            return [];
        }
    }

    // Caller holds the lock.
    private async Task WriteManifestAsync(IReadOnlyList<ImageModelRegistryEntry> entries, CancellationToken ct)
    {
        Directory.CreateDirectory(_modelsDirectory);
        var document = new ManifestDocument
        {
            Models = entries.ToList()
        };

        var tempPath = _manifestPath + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, ct).ConfigureAwait(false);
        }

        // Atomic replace so a crash mid-write never leaves a half-written manifest.
        File.Move(tempPath, _manifestPath, overwrite: true);
    }

    private sealed class ManifestDocument
    {
        public List<ImageModelRegistryEntry> Models { get; set; } = [];
    }
}
