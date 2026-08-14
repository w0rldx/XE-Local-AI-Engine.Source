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

    internal async Task<IReadOnlyList<GgufModelRegistryEntry>> ListAllAsync(CancellationToken ct)
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
            entry = EnsureRevision(entry);
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

    /// <summary>Inserts a new exact entry without replacing any model-name or backing-path collision.</summary>
    internal async Task InsertIfAbsentAsync(GgufModelRegistryEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry = EnsureRevision(entry);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = (await LoadEntriesAsync(ct).ConfigureAwait(false)).ToList();
            var normalizedPath = NormalizeLocalPath(entry.LocalPath);
            if (entries.Any(existing => existing == entry
                                        && string.Equals(existing.RegistryRevision, entry.RegistryRevision, StringComparison.Ordinal)))
            {
                return;
            }

            if (entries.Any(existing => string.Equals(existing.ModelName, entry.ModelName, StringComparison.OrdinalIgnoreCase)
                                        || PathComparer.Equals(NormalizeLocalPath(existing.LocalPath), normalizedPath)))
            {
                throw new IOException("The model registry destination already exists.");
            }

            entries.Add(entry);
            await WriteManifestAsync(entries, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Removes an entry only while its complete immutable value and revision still match.</summary>
    internal async Task<bool> RemoveExactAsync(GgufModelRegistryEntry expected, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        expected = EnsureRevision(expected);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = (await LoadEntriesAsync(ct).ConfigureAwait(false)).ToList();
            var index = entries.FindIndex(entry => entry == expected
                                                   && string.Equals(entry.RegistryRevision, expected.RegistryRevision, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            entries.RemoveAt(index);
            await WriteManifestAsync(entries, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    internal async Task<IReadOnlyList<GgufModelRegistryEntry>?> RemoveAliasSetIfMatchAsync(
        IReadOnlyList<GgufModelRegistryEntry> expectedAliases,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expectedAliases);
        if (expectedAliases.Count == 0)
        {
            throw new ArgumentException("At least one expected registry alias is required.", nameof(expectedAliases));
        }

        var expected = expectedAliases.Select(EnsureRevision)
                                      .OrderBy(static entry => entry.ModelName, StringComparer.OrdinalIgnoreCase)
                                      .ThenBy(static entry => entry.ModelName, StringComparer.Ordinal)
                                      .ToArray();
        var expectedPath = NormalizeLocalPath(expected[0].LocalPath);
        if (expected.Any(entry => !PathComparer.Equals(NormalizeLocalPath(entry.LocalPath), expectedPath)))
        {
            throw new ArgumentException("Every expected alias must reference the same backing weight.", nameof(expectedAliases));
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = (await LoadManifestEntriesForMutationAsync(ct).ConfigureAwait(false)).ToList();
            var current = entries.Where(entry => PathComparer.Equals(NormalizeLocalPath(entry.LocalPath), expectedPath))
                                 .OrderBy(static entry => entry.ModelName, StringComparer.OrdinalIgnoreCase)
                                 .ThenBy(static entry => entry.ModelName, StringComparer.Ordinal)
                                 .ToArray();
            if (!current.SequenceEqual(expected))
            {
                return null;
            }

            entries.RemoveAll(entry => PathComparer.Equals(NormalizeLocalPath(entry.LocalPath), expectedPath));
            await WriteManifestAsync(entries, ct).ConfigureAwait(false);
            return Array.AsReadOnly(current);
        }
        finally
        {
            _lock.Release();
        }
    }

    internal async Task<bool> RestoreAliasSetIfMatchAsync(IReadOnlyList<GgufModelRegistryEntry> expectedAliases,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expectedAliases);
        if (expectedAliases.Count == 0)
        {
            throw new ArgumentException("At least one expected registry alias is required.", nameof(expectedAliases));
        }

        var expected = expectedAliases.Select(EnsureRevision)
                                      .OrderBy(static entry => entry.ModelName, StringComparer.OrdinalIgnoreCase)
                                      .ThenBy(static entry => entry.ModelName, StringComparer.Ordinal)
                                      .ToArray();
        var expectedPath = NormalizeLocalPath(expected[0].LocalPath);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = (await LoadManifestEntriesForMutationAsync(ct).ConfigureAwait(false)).ToList();
            var current = entries.Where(entry => PathComparer.Equals(NormalizeLocalPath(entry.LocalPath), expectedPath)
                                                 || expected.Any(expectedEntry => string.Equals(expectedEntry.ModelName,
                                                     entry.ModelName,
                                                     StringComparison.OrdinalIgnoreCase)))
                                 .OrderBy(static entry => entry.ModelName, StringComparer.OrdinalIgnoreCase)
                                 .ThenBy(static entry => entry.ModelName, StringComparer.Ordinal)
                                 .ToArray();
            if (current.SequenceEqual(expected))
            {
                return true;
            }

            if (current.Length != 0)
            {
                return false;
            }

            entries.AddRange(expected);
            await WriteManifestAsync(entries, ct).ConfigureAwait(false);
            return true;
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
            return await RecoverAndPersistAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await using var stream = new FileStream(_manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var manifest = await JsonSerializer
                                 .DeserializeAsync<ManifestDocument>(stream, SerializerOptions, ct)
                                 .ConfigureAwait(false);

            if (manifest?.Models is null)
            {
                return await RecoverAndPersistAsync(ct).ConfigureAwait(false);
            }

            var entries = new List<GgufModelRegistryEntry>(manifest.Models.Count);
            foreach (var entry in manifest.Models.Where(static entry => entry.LocalPath is not null))
            {
                if (!File.Exists(entry.LocalPath))
                {
                    continue;
                }

                try
                {
                    var computed = GgufRegistryRevision.ComputeV1(entry, _modelsDirectory);
                    if (entry.RegistryRevision is not null
                        && !string.Equals(entry.RegistryRevision, computed, StringComparison.Ordinal))
                    {
                        _logger.LogWarning("Skipping GGUF registry entry {ModelName}: RegistryRevisionMismatch.", entry.ModelName);
                        continue;
                    }

                    entries.Add(entry with { RegistryRevision = computed });
                }
                catch (ArgumentException)
                {
                    _logger.LogWarning("Skipping GGUF registry entry {ModelName}: invalid contained path metadata.", entry.ModelName);
                }
            }

            var (reconciled, changed) = await ReconcileWithSidecarsAsync(entries, ct).ConfigureAwait(false);
            if (changed)
            {
                await WriteManifestAsync(reconciled, ct).ConfigureAwait(false);
            }

            return reconciled;
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "GGUF registry manifest is corrupt. Rebuilding by rescanning the models directory.");
            return await RecoverAndPersistAsync(ct).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "GGUF registry manifest could not be read. Rebuilding by rescanning the models directory.");
            return await RecoverAndPersistAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<GgufModelRegistryEntry>> LoadManifestEntriesForMutationAsync(CancellationToken ct)
    {
        if (!File.Exists(_manifestPath))
        {
            return [];
        }

        await using var stream = new FileStream(_manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var manifest = await JsonSerializer.DeserializeAsync<ManifestDocument>(stream, SerializerOptions, ct).ConfigureAwait(false);
        if (manifest?.Models is null)
        {
            throw new IOException("The GGUF registry manifest is invalid.");
        }

        return manifest.Models.Where(static entry => entry.LocalPath is not null).Select(EnsureRevision).ToArray();
    }

    // Best-effort rebuild from the .gguf files on disk when the manifest is unavailable. Metadata not derivable from the
    // file alone (sha256, source revision) is left empty — the store re-verifies on the next ensure. The role stays
    // Unknown EXCEPT for a speculative-decoding drafter, which the file name alone identifies (see GgufDraftModel).
    private async Task<IReadOnlyList<GgufModelRegistryEntry>> RescanAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_modelsDirectory))
        {
            return [];
        }

        var entries = new List<GgufModelRegistryEntry>();
        foreach (var path in Directory.EnumerateFiles(_modelsDirectory, "*.gguf", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            var sidecarPath = path + GgufAcquisitionSidecar.Suffix;
            if (File.Exists(sidecarPath))
            {
                var metadata = await GgufAcquisitionSidecar.ReadValidAsync(sidecarPath, path, _modelsDirectory, ct).ConfigureAwait(false);
                if (metadata is null)
                {
                    _logger.LogWarning("Skipping acquired GGUF {FileName}: invalid acquisition sidecar.", fileName);
                    continue;
                }

                var recovered = GgufAcquisitionSidecar.ToRegistryEntry(metadata, path, _modelsDirectory);
                if (!string.Equals(recovered.RegistryRevision, metadata.RegistryRevision, StringComparison.Ordinal))
                {
                    _logger.LogWarning("Skipping acquired GGUF {FileName}: RegistryRevisionMismatch.", fileName);
                    continue;
                }

                entries.Add(recovered);
                continue;
            }

            if (IsDeterministicAcquisitionFileName(fileName))
            {
                _logger.LogWarning("Skipping acquired GGUF {FileName}: recovery sidecar is missing.", fileName);
                continue;
            }

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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
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
            var legacy = new GgufModelRegistryEntry
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
            };
            entries.Add(EnsureRevision(legacy));
        }

        return entries;
    }

    private async Task<IReadOnlyList<GgufModelRegistryEntry>> RecoverAndPersistAsync(CancellationToken ct)
    {
        var entries = await RescanAsync(ct).ConfigureAwait(false);
        if (entries.Count > 0)
        {
            await WriteManifestAsync(entries, ct).ConfigureAwait(false);
        }

        return entries;
    }

    private GgufModelRegistryEntry EnsureRevision(GgufModelRegistryEntry entry)
    {
        var computed = GgufRegistryRevision.ComputeV1(entry, _modelsDirectory);
        if (entry.RegistryRevision is not null && !string.Equals(entry.RegistryRevision, computed, StringComparison.Ordinal))
        {
            throw new ArgumentException("The registry revision does not match the material entry value.", nameof(entry));
        }

        return entry with { RegistryRevision = computed };
    }

    private async Task<(IReadOnlyList<GgufModelRegistryEntry> Entries, bool Changed)> ReconcileWithSidecarsAsync(
        IReadOnlyList<GgufModelRegistryEntry> manifestEntries,
        CancellationToken ct)
    {
        if (!Directory.Exists(_modelsDirectory))
        {
            return ([], manifestEntries.Count != 0);
        }

        var entries = manifestEntries.ToList();
        var changed = false;
        foreach (var path in Directory.EnumerateFiles(_modelsDirectory, "*.gguf", SearchOption.TopDirectoryOnly))
        {
            var sidecarPath = path + GgufAcquisitionSidecar.Suffix;
            if (File.Exists(sidecarPath))
            {
                var metadata = await GgufAcquisitionSidecar.ReadShapeValidAsync(sidecarPath, path, _modelsDirectory, ct).ConfigureAwait(false);
                if (metadata is null)
                {
                    changed |= RemoveEntriesForPath(entries, path) > 0;
                    _logger.LogWarning("Skipping acquired GGUF {FileName}: invalid acquisition sidecar.", Path.GetFileName(path));
                    continue;
                }

                var recovered = GgufAcquisitionSidecar.ToRegistryEntry(metadata, path, _modelsDirectory);
                if (!string.Equals(recovered.RegistryRevision, metadata.RegistryRevision, StringComparison.Ordinal))
                {
                    changed |= RemoveEntriesForPath(entries, path) > 0;
                    _logger.LogWarning("Skipping acquired GGUF {FileName}: RegistryRevisionMismatch.", Path.GetFileName(path));
                    continue;
                }

                var samePath = entries.Where(entry => PathComparer.Equals(NormalizeLocalPath(entry.LocalPath), NormalizeLocalPath(path)))
                                      .ToArray();
                if (samePath.Length == 1
                    && string.Equals(samePath[0].ModelName, recovered.ModelName, StringComparison.Ordinal)
                    && string.Equals(samePath[0].RegistryRevision, recovered.RegistryRevision, StringComparison.Ordinal))
                {
                    continue;
                }

                var nameCollision = entries.Any(entry => !samePath.Contains(entry)
                                                         && string.Equals(entry.ModelName, recovered.ModelName,
                                                             StringComparison.OrdinalIgnoreCase));
                if (nameCollision)
                {
                    _logger.LogWarning("Skipping acquired GGUF {FileName}: its model name conflicts with another registry path.",
                        Path.GetFileName(path));
                    continue;
                }

                // Recovery is the only normal registry path that needs to establish file-byte integrity. Existing exact
                // manifest rows are deliberately served from stable registry/sidecar facts; explicit snapshots perform
                // the full content verification required for mutation and benchmark boundaries.
                if (await GgufAcquisitionSidecar.ReadValidAsync(sidecarPath, path, _modelsDirectory, ct).ConfigureAwait(false) is null)
                {
                    changed |= RemoveEntriesForPath(entries, path) > 0;
                    _logger.LogWarning("Skipping acquired GGUF {FileName}: acquisition content verification failed.", Path.GetFileName(path));
                    continue;
                }

                entries.RemoveAll(entry => samePath.Contains(entry));
                entries.Add(recovered);
                changed = true;
                continue;
            }

            if (IsDeterministicAcquisitionFileName(Path.GetFileName(path)))
            {
                changed |= RemoveAcquiredEntriesForPath(entries, path) > 0;
                _logger.LogWarning("Skipping acquired GGUF {FileName}: recovery sidecar is missing.", Path.GetFileName(path));
            }
        }

        var invalidAcquisitions = entries.Where(entry => entry.Origin is not null
                                                          && !File.Exists(entry.LocalPath + GgufAcquisitionSidecar.Suffix))
                                         .ToArray();
        if (invalidAcquisitions.Length > 0)
        {
            entries.RemoveAll(entry => invalidAcquisitions.Contains(entry));
            changed = true;
        }

        return (entries, changed);
    }

    private static int RemoveEntriesForPath(List<GgufModelRegistryEntry> entries, string path)
    {
        return entries.RemoveAll(entry => PathComparer.Equals(NormalizeLocalPath(entry.LocalPath), NormalizeLocalPath(path)));
    }

    private static int RemoveAcquiredEntriesForPath(List<GgufModelRegistryEntry> entries, string path)
    {
        return entries.RemoveAll(entry => entry.Origin is not null
                                          && PathComparer.Equals(NormalizeLocalPath(entry.LocalPath), NormalizeLocalPath(path)));
    }

    private static bool IsDeterministicAcquisitionFileName(string fileName)
    {
        if (!string.Equals(Path.GetExtension(fileName), ".gguf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var separator = stem.LastIndexOf('-');
        if (separator <= 0 || stem.Length - separator - 1 != 24
            || !stem.AsSpan(separator + 1).ToString().All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            return false;
        }

        var prefix = stem[..separator];
        var quant = GgufQuantParser.TryParse(prefix);
        return quant is not null
               && (prefix.EndsWith(quant, StringComparison.OrdinalIgnoreCase)
                   || prefix.EndsWith(quant.Replace("UD-", "UD_", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));
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
