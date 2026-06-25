namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

/// <summary>
///     File-based <see cref="IGgufModelRegistry" /> backed by a JSON manifest (<c>index.json</c>) under the models
///     directory. The single owner of the manifest (read + write under a semaphore); <see cref="HuggingFaceGgufStore" />
///     calls the internal write methods, while the public interface stays read-only. Self-heals by rescanning the
///     directory when the manifest is missing or corrupt.
/// </summary>
internal sealed class GgufModelRegistry : IGgufModelRegistry, IDisposable
{
    private const string ManifestFileName = "index.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly SemaphoreSlim _lock = new(initialCount: 1, maxCount: 1);
    private readonly ILogger<GgufModelRegistry> _logger;
    private readonly string _manifestPath;

    private readonly string _modelsDirectory;

    public GgufModelRegistry(HuggingFaceOptions options, ILogger<GgufModelRegistry> logger)
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
    public async Task<IReadOnlyList<GgufModelRegistryEntry>> ListAsync(CancellationToken ct)
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
    public async Task<GgufModelRegistryEntry?> FindAsync(string modelName, CancellationToken ct)
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

    /// <summary>Inserts or replaces the entry keyed by <see cref="GgufModelRegistryEntry.ModelName" />.</summary>
    public async Task UpsertAsync(GgufModelRegistryEntry entry, CancellationToken ct)
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

    // Reads the manifest, self-healing to a directory rescan when it is missing or corrupt. Caller holds the lock.
    private async Task<IReadOnlyList<GgufModelRegistryEntry>> LoadEntriesAsync(CancellationToken ct)
    {
        if (!File.Exists(_manifestPath))
        {
            return Rescan();
        }

        try
        {
            await using var stream = new FileStream(_manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var manifest = await JsonSerializer
                                 .DeserializeAsync<ManifestDocument>(stream, SerializerOptions, ct)
                                 .ConfigureAwait(false);

            if (manifest?.Models is null)
            {
                return Rescan();
            }

            // Drop entries whose backing file disappeared since the manifest was written (manual deletion).
            return manifest.Models
                           .Where(entry => File.Exists(entry.LocalPath))
                           .ToList();
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "GGUF registry manifest is corrupt. Rebuilding by rescanning the models directory.");
            return Rescan();
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "GGUF registry manifest could not be read. Rebuilding by rescanning the models directory.");
            return Rescan();
        }
    }

    // Best-effort rebuild from the .gguf files on disk when the manifest is unavailable. Metadata not derivable from the
    // file alone (sha256, source revision, role) is left empty/Unknown — the store re-verifies on the next ensure.
    private IReadOnlyList<GgufModelRegistryEntry> Rescan()
    {
        if (!Directory.Exists(_modelsDirectory))
        {
            return [];
        }

        var entries = new List<GgufModelRegistryEntry>();
        foreach (var path in Directory.EnumerateFiles(_modelsDirectory, "*.gguf", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            var quant = GgufQuantParser.TryParse(fileName);
            if (quant is null)
            {
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(path);
            }
            catch (IOException)
            {
                continue;
            }

            // A rescanned entry has no recoverable repo/revision; key it by the file stem so it is at least resolvable.
            var modelName = GgufModelName.Format(Path.GetFileNameWithoutExtension(fileName), quant);
            entries.Add(new GgufModelRegistryEntry
            {
                ModelName = modelName,
                RepoId = Path.GetFileNameWithoutExtension(fileName),
                FileName = fileName,
                Quant = quant,
                LocalPath = path,
                SizeBytes = info.Length,
                Sha256 = null,
                SourceRevision = string.Empty,
                DownloadedAtUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                Role = GgufRole.Unknown
            });
        }

        return entries;
    }

    // Caller holds the lock.
    private async Task WriteManifestAsync(IReadOnlyList<GgufModelRegistryEntry> entries, CancellationToken ct)
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
        public List<GgufModelRegistryEntry> Models { get; set; } = [];
    }
}
