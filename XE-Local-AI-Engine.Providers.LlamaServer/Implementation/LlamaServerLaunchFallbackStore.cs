namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
///     load; the snapshot is refreshed under the same lock on every write. The read-merge-replace additionally tries to
///     hold an OS file lock on a sibling <c>.lock</c> file: WHILE THAT LOCK IS HELD two node processes writing at once
///     cannot lose each other's verdict. Acquisition is bounded, and a write that could not take it proceeds unlocked
///     (logged at Warning) with the old in-process-only ceiling, where a sibling write landing inside that window is
///     still lost.
///     Legacy backend-only entries (written before the store was keyed by KV type) are ignored on load and dropped from
///     the file on the first read, so an old un-keyed verdict can no longer make the node's KV-cache-type setting inert.
/// </remarks>
public sealed class LlamaServerLaunchFallbackStore : ILlamaServerLaunchFallbackStore, IDisposable
{
    private const string StateFileName = "llama-launch-fallback.json";

    // Cross-process write lock: a few short attempts, then give up and merge with the in-process lock alone. The whole
    // budget stays well under a spawn's tolerance because this runs on the launch path.
    private const int LockAttempts = 4;

    private const string LockFileName = StateFileName + ".lock";

    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(25);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _lock = new(initialCount: 1, maxCount: 1);
    private readonly string _lockPath;
    private readonly ILogger _logger;
    private readonly string _statePath;

    // Null until first load; then the authoritative in-memory snapshot of disabled "{Variant}:{kvType}" keys
    // (case-insensitive).
    private HashSet<string>? _disabled;

    /// <summary>Creates the store under <paramref name="cacheRoot" /> (defaulting to the shared app cache root).</summary>
    public LlamaServerLaunchFallbackStore(string? cacheRoot = null, ILogger<LlamaServerLaunchFallbackStore>? logger = null)
    {
        var root = string.IsNullOrWhiteSpace(cacheRoot) ? DefaultCacheRoot() : cacheRoot;
        _statePath = Path.Combine(root, StateFileName);
        _lockPath = Path.Combine(root, LockFileName);
        _logger = logger ?? NullLogger<LlamaServerLaunchFallbackStore>.Instance;
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
            // Shared with writers and deleters, not just other readers: File.OpenRead shares Read only, and on Windows
            // a lock-free sibling read taken that way blocks the File.Move(..., overwrite: true) below — turning a
            // ready safe-retry spawn into a launch failure. Readers still see whole documents either way, because the
            // replace is atomic.
            await using var stream = new FileStream(_statePath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite | FileShare.Delete
                });
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
        // The re-read and the replace are one cross-process critical section, held on a sibling .lock file rather than
        // on the state file itself: an exclusive handle on the state file would make the atomic File.Move over it fail
        // on Windows, and readers take no lock at all, so they keep seeing whole documents throughout.
        // ponytail: a lock this process could not acquire (a sibling holding it past the retry budget, or an unwritable
        // cache root) degrades to the in-process lock alone, which is the old ceiling — a sibling write landing inside
        // that window is lost. Accepted: it costs one failed spawn that re-records the verdict.
        using var crossProcessLock = await TryAcquireWriteLockAsync(ct).ConfigureAwait(false);
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

    /// <summary>
    ///     Takes the cross-process write lock, or null when it could not be had: a sibling still holding it after the
    ///     retry budget, or a cache root this process cannot write to. Either way the write proceeds unlocked, and both
    ///     paths are logged at Warning — the degraded merge is the one thing a silent return would hide.
    /// </summary>
    private async Task<FileStream?> TryAcquireWriteLockAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= LockAttempts; attempt++)
        {
            try
            {
                return CreateOwnerOnly(_lockPath, FileMode.OpenOrCreate);
            }
            catch (IOException)
            {
                // Held by a sibling node process: back off and retry.
            }
            catch (UnauthorizedAccessException)
            {
                // Unwritable cache root: retrying cannot make it writable. The READ path's legacy drop tolerates this
                // (it swallows the failed rewrite), but the safe-retry caller of the WRITE does not — the state write
                // below fails the same way and aborts the launch — so say so rather than degrading silently.
                _logger.LogWarning("Could not create the llama-server launch-fallback write lock at {LockPath} (the cache root is not writable); merging under the in-process lock only, so a concurrent sibling write may be lost.",
                    _lockPath);
                return null;
            }

            if (attempt < LockAttempts)
            {
                await Task.Delay(LockRetryDelay, ct).ConfigureAwait(false);
            }
        }

        _logger.LogWarning("Could not take the llama-server launch-fallback write lock at {LockPath} after {Attempts} attempts; merging under the in-process lock only, so a concurrent sibling write may be lost.",
            _lockPath,
            LockAttempts);
        return null;
    }

    private static FileStream CreateOwnerOnly(string path, FileMode mode = FileMode.Create)
    {
        var options = new FileStreamOptions
        {
            Mode = mode,
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
