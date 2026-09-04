namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="ILlamaServerLaunchFallbackStore" />: persists the set of (GPU backend, KV-cache type) pairs
///     whose optimized launch config (quantized KV cache + flash attention) has proven unable to reach readiness on this
///     host to <c>llama-launch-fallback.json</c> under the cache root.
/// </summary>
/// <remarks>
///     Mirrors <see cref="InstalledRuntimeStore" />: tolerant deserialize (absent/corrupt → empty), atomic temp-file
///     write then move-with-overwrite, owner-only (0600) permissions on non-Windows. An in-memory snapshot backs the
///     read so <see cref="IsOptimizedConfigDisabledAsync" /> never touches disk on the spawn hot path after the first
///     load; the snapshot is refreshed under the same lock on every write.
///     Legacy backend-only entries (written before the store was keyed by KV type) are ignored on load and dropped from
///     the file on the first read, so an old un-keyed verdict can no longer make the node's KV-cache-type setting inert.
/// </remarks>
public sealed class LlamaServerLaunchFallbackStore : ILlamaServerLaunchFallbackStore, IDisposable
{
    private const string StateFileName = "llama-launch-fallback.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _lock = new(initialCount: 1, maxCount: 1);
    private readonly string _statePath;

    // Null until first load; then the authoritative in-memory snapshot of disabled "{Variant}:{kvType}" keys
    // (case-insensitive).
    private HashSet<string>? _disabled;

    /// <summary>Creates the store under <paramref name="cacheRoot" /> (defaulting to the shared app cache root).</summary>
    public LlamaServerLaunchFallbackStore(string? cacheRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(cacheRoot) ? DefaultCacheRoot() : cacheRoot;
        _statePath = Path.Combine(root, StateFileName);
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    /// <inheritdoc />
    public async Task<bool> IsOptimizedConfigDisabledAsync(GpuVariant variant, string kvCacheType, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var disabled = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            return disabled.Contains(PairKey(variant, kvCacheType));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DisableOptimizedConfigAsync(GpuVariant variant, string kvCacheType, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var disabled = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            if (!disabled.Add(PairKey(variant, kvCacheType)))
            {
                return; // Already recorded — idempotent no-op, no re-write.
            }

            await PersistAsync(disabled, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string PairKey(GpuVariant variant, string kvCacheType) =>
        string.Concat(variant.ToString(), ":", (kvCacheType ?? string.Empty).Trim());

    /// <summary>Loads the snapshot once, dropping any legacy un-keyed entries from the file on that first read.</summary>
    /// <remarks>Callers already hold <see cref="_lock" />, which is what makes the one-time rewrite safe.</remarks>
    private async Task<HashSet<string>> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_disabled is { } cached)
        {
            return cached;
        }

        var (disabled, hadLegacy) = await LoadAsync(ct).ConfigureAwait(false);
        _disabled = disabled;
        if (hadLegacy)
        {
            try
            {
                await PersistAsync(disabled, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unwritable cache root must not fault a spawn: the snapshot above is already clean, and the next
                // process start repeats the drop.
            }
        }

        return disabled;
    }

    private async Task<(HashSet<string> Disabled, bool HadLegacy)> LoadAsync(CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hadLegacy = false;
        if (!File.Exists(_statePath))
        {
            return (set, hadLegacy);
        }

        try
        {
            await using var stream = File.OpenRead(_statePath);
            var state = await JsonSerializer.DeserializeAsync<LlamaServerLaunchFallbackState>(stream, SerializerOptions, ct).ConfigureAwait(false);

            // Legacy, backend-only entries carry no KV type, so they cannot say which config failed. They are ignored
            // and the file is rewritten without them rather than being read as "every KV type on this backend", which
            // made the node's KV-cache-type setting inert on any host that recorded one.
            hadLegacy = state?.DisabledOptimizedVariants is { Count: > 0 };

            if (state?.DisabledOptimizedConfigs is { } configs)
            {
                foreach (var key in configs.Where(static key => !string.IsNullOrWhiteSpace(key)))
                {
                    set.Add(key);
                }
            }
        }
        catch (JsonException)
        {
            // Corrupt state file → treat as nothing disabled (the optimized config is retried; a real failure re-records).
        }
        catch (IOException)
        {
            // Unreadable → treat as nothing disabled.
        }

        return (set, hadLegacy);
    }

    private async Task PersistAsync(HashSet<string> disabled, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);

        // The file is USER-level, so several node processes share it and each write replaces the whole document. Fold
        // whatever is on disk back in first, or a sibling process's verdict is silently dropped. Legacy names are
        // ignored by the load, so they stay dropped.
        // ponytail: the re-read is guarded by the in-process lock only, so a sibling write landing between it and the
        // File.Move below is still lost. Accepted — a lost verdict costs one failed spawn that re-records it. Upgrade
        // path: hold an OS file lock (FileShare.None on the state file) across the read and the move.
        var (onDisk, _) = await LoadAsync(ct).ConfigureAwait(false);
        disabled.UnionWith(onDisk);

        // Every entry is a "{Variant}:{kvType}" pair; the legacy list is always written empty.
        var state = new LlamaServerLaunchFallbackState([], [.. disabled]);
        var tempPath = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = CreateOwnerOnly(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, state, SerializerOptions, ct).ConfigureAwait(false);
            }

            File.Move(tempPath, _statePath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

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

    private static void TryDeleteFile(string path)
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
            // Best-effort cleanup of a temp write; ignore.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temp write; ignore.
        }
    }

    private static string DefaultCacheRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XE-Local-AI-Engine");
    }
}

/// <summary>Persisted shape for <see cref="LlamaServerLaunchFallbackStore" />: the launch configs proven unable to reach readiness.</summary>
/// <param name="DisabledOptimizedVariants">
///     LEGACY: backend names (<see cref="GpuVariant" />) recorded before the store was keyed by KV type. The property
///     exists only so an old file still deserializes — the entries are ignored and dropped on the first read, and this
///     list is always written empty.
/// </param>
/// <param name="DisabledOptimizedConfigs">
///     <c>"{Variant}:{kvType}"</c> keys whose KV-quant + flash-attention config failed readiness. A <c>q4_0</c> failure
///     here leaves <c>q8_0</c> on the same backend enabled.
/// </param>
public sealed record LlamaServerLaunchFallbackState(IReadOnlyList<string> DisabledOptimizedVariants,
    IReadOnlyList<string>? DisabledOptimizedConfigs = null);
