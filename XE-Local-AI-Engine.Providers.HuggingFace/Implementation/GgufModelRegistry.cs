namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
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

    // Backing files are compared by their canonicalized absolute path; case-insensitively on Windows (its file system is),
    // ordinally elsewhere. Used to collapse two registry entries that resolve to the SAME .gguf file on disk.
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

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
            // Public view: collapse any duplicate-path entries (a legacy first-download alias sharing one file) so a
            // single .gguf is listed once. This is the load-time migration for manifests written before the upsert fix —
            // it never touches the file, only the in-memory view; a later write persists the collapse.
            var entries = await LoadEntriesAsync(ct).ConfigureAwait(false);
            return CollapseDuplicatePaths(entries);
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

    /// <summary>
    ///     Inserts or replaces the entry keyed by <see cref="GgufModelRegistryEntry.ModelName" />, and also removes any
    ///     prior entry that resolves to the SAME backing file. The self-healing rescan (triggered when the manifest is
    ///     absent, e.g. the very first download) registers the just-written .gguf under a filename-derived name; without
    ///     the same-path removal the canonical upsert that follows would append a second entry to one file. The incoming
    ///     entry carries canonical repo metadata (verified hash/revision), so it is preferred over the alias.
    /// </summary>
    public async Task UpsertAsync(GgufModelRegistryEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = (await LoadEntriesAsync(ct).ConfigureAwait(false)).ToList();
            var normalizedPath = NormalizeLocalPath(entry.LocalPath);
            entries.RemoveAll(existing =>
                string.Equals(existing.ModelName, entry.ModelName, StringComparison.Ordinal)
                || PathComparer.Equals(NormalizeLocalPath(existing.LocalPath), normalizedPath));
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

    /// <summary>
    ///     Removes EVERY entry whose backing file resolves to <paramref name="localPath" />, atomically. A legacy first
    ///     download registered one file under two names (a filename alias plus the canonical repo id); deleting through
    ///     either identity must leave no manifest entry pointing at the now-removed file. Returns the count removed;
    ///     idempotent (no match is a no-op).
    /// </summary>
    public async Task<int> RemoveByLocalPathAsync(string localPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        var normalizedPath = NormalizeLocalPath(localPath);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = (await LoadEntriesAsync(ct).ConfigureAwait(false)).ToList();
            var removed = entries.RemoveAll(existing => PathComparer.Equals(NormalizeLocalPath(existing.LocalPath), normalizedPath));
            if (removed > 0)
            {
                await WriteManifestAsync(entries, ct).ConfigureAwait(false);
            }

            return removed;
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
    // file alone (sha256, source revision) is left empty — the store re-verifies on the next ensure. The role stays
    // Unknown EXCEPT for a speculative-decoding drafter, which the file name alone identifies (see GgufDraftModel).
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

            // A parseable quant alone does NOT make a file a chat model: a speculative-decoding drafter
            // (mtp-<model>-Q8_0.gguf) parses to Q8_0 like any base quant, and a rescan used to register it as an
            // ordinary model — a 0.4 GB "Q8_0" sitting beside the real one. Mark its quant and role so it keeps a
            // distinct identity and stays out of the chat surfaces (the same rule discovery applies to the repo side).
            var isDraft = GgufDraftModel.IsDraftFile(fileName);
            if (isDraft)
            {
                quant = GgufDraftModel.MarkQuant(quant);
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
                Role = isDraft ? GgufRole.Draft : GgufRole.Unknown
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

    // Collapses entries that resolve to the same backing file into one, keeping the most canonical (verified hash/
    // revision, real repo id, known role). Order is otherwise preserved. Never touches disk.
    private static IReadOnlyList<GgufModelRegistryEntry> CollapseDuplicatePaths(IReadOnlyList<GgufModelRegistryEntry> entries)
    {
        var indexByPath = new Dictionary<string, int>(PathComparer);
        var result = new List<GgufModelRegistryEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var key = NormalizeLocalPath(entry.LocalPath);
            if (indexByPath.TryGetValue(key, out var existingIndex))
            {
                result[existingIndex] = PreferCanonical(result[existingIndex], entry);
            }
            else
            {
                indexByPath[key] = result.Count;
                result.Add(entry);
            }
        }

        return result;
    }

    // Prefers the entry carrying more canonical provenance. A rescan-derived alias has a filename stem repo id, an empty
    // revision, a null hash, and an Unknown role; a canonically-registered download has the opposite.
    private static GgufModelRegistryEntry PreferCanonical(GgufModelRegistryEntry left, GgufModelRegistryEntry right)
    {
        return CanonicalScore(right) > CanonicalScore(left) ? right : left;
    }

    private static int CanonicalScore(GgufModelRegistryEntry entry)
    {
        var score = 0;
        if (entry.RepoId.Contains('/', StringComparison.Ordinal))
        {
            score++;
        }

        if (!string.IsNullOrEmpty(entry.SourceRevision))
        {
            score++;
        }

        if (!string.IsNullOrEmpty(entry.Sha256))
        {
            score++;
        }

        if (entry.Role != GgufRole.Unknown)
        {
            score++;
        }

        return score;
    }

    // Canonicalizes a stored path for identity comparison (collapses '.'/'..' and relative forms). A file that cannot be
    // canonicalized falls back to its original string so a malformed path never throws on a list/upsert.
    private static string NormalizeLocalPath(string localPath)
    {
        try
        {
            return Path.GetFullPath(localPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return localPath;
        }
    }

    private sealed class ManifestDocument
    {
        public List<GgufModelRegistryEntry> Models { get; set; } = [];
    }
}
