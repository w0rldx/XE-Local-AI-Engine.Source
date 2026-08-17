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

    internal async Task<IReadOnlyList<GgufModelRegistryEntry>?> RemoveAliasSetIfMatchAsync(IReadOnlyList<GgufModelRegistryEntry> expectedAliases,
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
                                 .DeserializeAsync<RawManifestDocument>(stream, SerializerOptions, ct)
                                 .ConfigureAwait(false);

            if (manifest?.Models is null)
            {
                return await RecoverAndPersistAsync(ct).ConfigureAwait(false);
            }

            var entries = new List<GgufModelRegistryEntry>(manifest.Models.Count);
            foreach (var element in manifest.Models)
            {
                // Per-ENTRY deserialization. A row this build cannot understand — most concretely an origin string a
                // newer build writes and this one has no member for, which the strict origin converter rejects — is
                // skipped with a warning instead of failing the whole document and triggering a full directory rescan
                // that would demote every other model to a legacy entry.
                GgufModelRegistryEntry? entry;
                try
                {
                    entry = element.Deserialize<GgufModelRegistryEntry>(SerializerOptions);
                }
                catch (JsonException exception)
                {
                    _logger.LogWarning(exception, "Skipping an unreadable GGUF registry entry.");
                    continue;
                }

                if (entry?.LocalPath is null || !File.Exists(entry.LocalPath))
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

                    entries.Add(entry with
                    {
                        RegistryRevision = computed
                    });
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

        return entry with
        {
            RegistryRevision = computed
        };
    }

    private async Task<SidecarReconciliation> ReconcileWithSidecarsAsync(IReadOnlyList<GgufModelRegistryEntry> manifestEntries,
        CancellationToken ct)
    {
        if (!Directory.Exists(_modelsDirectory))
        {
            return new SidecarReconciliation([], manifestEntries.Count != 0);
        }

        var entries = manifestEntries.ToList();
        var changed = false;
        foreach (var path in Directory.EnumerateFiles(_modelsDirectory, "*.gguf", SearchOption.TopDirectoryOnly))
        {
            var sidecarPath = path + GgufAcquisitionSidecar.Suffix;
            if (File.Exists(sidecarPath))
            {
                // An unreadable/corrupt sidecar (metadata null) or one whose own claimed revision does not recompute
                // (RegistryRevisionMismatch) is repaired in place from the token-verified manifest entry when one
                // exists for this path — a registered model without a valid sidecar would be loadable but unable to
                // pass the sidecar-requiring mutation/deletion snapshot checks. When repair is impossible the entry is
                // still kept (the manifest passed its own RegistryRevision self-check in LoadEntriesAsync); only the
                // startup reaper cleans up genuine orphans.
                var metadata = await GgufAcquisitionSidecar.ReadShapeValidAsync(sidecarPath, path, _modelsDirectory, ct).ConfigureAwait(false);
                if (metadata is null)
                {
                    if (!await TryRepairSidecarAsync(entries, path, ct).ConfigureAwait(false))
                    {
                        _logger.LogWarning(
                            "Acquisition sidecar for GGUF {FileName} is unreadable or corrupt and could not be repaired; keeping any existing valid manifest entry without sidecar-derived recovery.",
                            Path.GetFileName(path));
                    }

                    continue;
                }

                var recovered = GgufAcquisitionSidecar.ToRegistryEntry(metadata, path, _modelsDirectory);
                if (!string.Equals(recovered.RegistryRevision, metadata.RegistryRevision, StringComparison.Ordinal))
                {
                    if (!await TryRepairSidecarAsync(entries, path, ct).ConfigureAwait(false))
                    {
                        _logger.LogWarning(
                            "Acquisition sidecar for GGUF {FileName} failed its RegistryRevision self-check and could not be repaired; keeping any existing valid manifest entry without sidecar-derived recovery.",
                            Path.GetFileName(path));
                    }

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
                    _logger.LogWarning("Acquisition content verification failed for GGUF {FileName}; keeping any existing valid manifest entry without sidecar-derived recovery.",
                        Path.GetFileName(path));
                    continue;
                }

                entries.RemoveAll(entry => samePath.Contains(entry));
                entries.Add(recovered);
                changed = true;
                continue;
            }

            if (IsDeterministicAcquisitionFileName(Path.GetFileName(path)))
            {
                _logger.LogWarning("Recovery sidecar for GGUF {FileName} is missing.", Path.GetFileName(path));
            }
        }

        // A missing acquisition sidecar, of any filename shape, is rewritten from its token-verified manifest entry so
        // the model stays consistent for the sidecar-requiring mutation/deletion snapshot checks. When repair is
        // impossible the entry is still kept (its manifest row passed the RegistryRevision self-check); the startup
        // reaper cleans up genuinely orphaned acquisition artifacts.
        foreach (var localPath in entries.Where(entry => entry.Origin is not null
                                                         && !File.Exists(entry.LocalPath + GgufAcquisitionSidecar.Suffix))
                                         .Select(entry => entry.LocalPath)
                                         .ToArray())
        {
            if (!await TryRepairSidecarAsync(entries, localPath, ct).ConfigureAwait(false))
            {
                _logger.LogWarning("Acquisition sidecar for GGUF {FileName} is missing and could not be repaired; keeping the existing valid manifest entry without sidecar-derived recovery.",
                    Path.GetFileName(localPath));
            }
        }

        return new SidecarReconciliation(entries, changed);
    }

    /// <summary>
    ///     Rewrites the acquisition sidecar for <paramref name="weightPath" /> from its single token-verified manifest
    ///     entry. The reconstruction is trusted only when round-tripping it back through
    ///     <see cref="GgufAcquisitionSidecar.ToRegistryEntry" /> reproduces the entry's exact verified
    ///     <c>RegistryRevision</c>, and the on-disk weight/projector sizes still match the entry — replaced bytes are
    ///     never blessed with fresh metadata. Best-effort: returns <see langword="false" /> instead of throwing.
    /// </summary>
    private async Task<bool> TryRepairSidecarAsync(List<GgufModelRegistryEntry> entries, string weightPath, CancellationToken ct)
    {
        var samePath = entries.Where(entry => PathComparer.Equals(NormalizeLocalPath(entry.LocalPath), NormalizeLocalPath(weightPath)))
                              .ToArray();
        if (samePath.Length != 1)
        {
            return false;
        }

        var entry = samePath[0];
        var metadata = GgufAcquisitionSidecar.FromRegistryEntry(entry, _modelsDirectory);
        if (metadata is null)
        {
            return false;
        }

        var roundTrip = GgufAcquisitionSidecar.ToRegistryEntry(metadata, entry.LocalPath, _modelsDirectory);
        if (!string.Equals(roundTrip.RegistryRevision, entry.RegistryRevision, StringComparison.Ordinal))
        {
            return false;
        }

        var sidecarPath = entry.LocalPath + GgufAcquisitionSidecar.Suffix;
        // The ".part" temp suffix keeps a crash-orphaned repair file inside the startup reaper's cleanup contract.
        var tempPath = sidecarPath + ".repair.part";
        try
        {
            if (new FileInfo(entry.LocalPath).Length != entry.SizeBytes)
            {
                return false;
            }

            if (entry.ProjectorLocalPath is { } projectorPath
                && (!File.Exists(projectorPath) || new FileInfo(projectorPath).Length != entry.ProjectorSizeBytes))
            {
                return false;
            }

            File.Delete(tempPath);
            await GgufAcquisitionSidecar.WriteAsync(tempPath, metadata, ct).ConfigureAwait(false);
            File.Move(tempPath, sidecarPath, overwrite: true);
            _logger.LogWarning("Repaired the missing or invalid acquisition sidecar for GGUF {FileName} from its verified manifest entry.",
                Path.GetFileName(entry.LocalPath));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(exception, "Could not repair the acquisition sidecar for GGUF {FileName}.", Path.GetFileName(entry.LocalPath));
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The orphaned ".part" temp is reclaimed by the startup reaper.
            }

            return false;
        }
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

    // Read-side shape for the tolerant load path: rows stay unparsed until each is converted individually, so one
    // unreadable row cannot fail the document. The mutation path deliberately keeps the strict ManifestDocument —
    // silently dropping a row there would delete it from the manifest on the write that follows.
    private sealed class RawManifestDocument
    {
        public List<JsonElement> Models { get; set; } = [];
    }

    /// <summary>The registry rows after the on-disk sidecar sweep, and whether they differ from the manifest that was read.</summary>
    private sealed record SidecarReconciliation(IReadOnlyList<GgufModelRegistryEntry> Entries, bool Changed);
}
