namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="ILlamaServerLaunchFallbackStore" />: persists to <c>llama-launch-fallback.json</c> under the
///     cache root the (GPU backend, KV-cache type) pairs whose optimized launch config has proven unable to reach
///     readiness on this host.
/// </summary>
/// <remarks>
///     The optimized config is a quantized KV cache plus flash attention. Storage mirrors
///     <see cref="InstalledRuntimeStore" />: tolerant deserialize, atomic temp-file write then move-with-overwrite,
///     owner-only 0600 permissions off Windows. An in-memory snapshot backs the read, so
///     <see cref="IsOptimizedConfigDisabledAsync" /> never touches disk on the spawn hot path after the first load.
///     Cross-process merge: docs/wiki/03-local-runtime-and-providers.md, "`LlamaServerProcessSupervisor` — process lifecycle".
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

    // Guards the in-memory snapshot only; disk writes never run under it, so a stalled write cannot hold up a reader.
    private readonly SemaphoreSlim _lock = new(initialCount: 1, maxCount: 1);

    // Keys published in memory whose write has not succeeded yet, guarded by _lock.
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _lockPath;
    private readonly ILogger _logger;

    // Serializes the disk writes, one at a time, outside _lock.
    private readonly SemaphoreSlim _persistLock = new(initialCount: 1, maxCount: 1);
    private readonly string _statePath;

    // Null until first load; then the authoritative in-memory snapshot of disabled "{Variant}:{kvType}" keys
    // (case-insensitive).
    private HashSet<string>? _disabled;

    // The single in-flight background re-persist, replaced under _lock only once it completed.
    private Task _repersist = Task.CompletedTask;
    private int _repersistFailureLogged;

    /// <summary>Creates the store under <paramref name="cacheRoot" /> (defaulting to the shared app cache root).</summary>
    public LlamaServerLaunchFallbackStore(string? cacheRoot = null, ILogger<LlamaServerLaunchFallbackStore>? logger = null)
    {
        var root = string.IsNullOrWhiteSpace(cacheRoot) ? RuntimeCacheDirectory.Resolve() : cacheRoot;
        _statePath = Path.Combine(root, StateFileName);
        _lockPath = Path.Combine(root, LockFileName);
        _logger = logger ?? NullLogger<LlamaServerLaunchFallbackStore>.Instance;
    }

    /// <summary>The latest background re-persist of an unwritten verdict. Test seam; it never faults.</summary>
    internal Task PendingRepersistForTesting => Volatile.Read(ref _repersist);

    public void Dispose()
    {
        // The semaphores are never disposed: a caller or the background re-persist may still be parked on one.
    }

    /// <inheritdoc />
    /// <remarks>A verdict whose write failed is written again by the read that finds it, on one background task at a time.</remarks>
    public async Task<bool> IsOptimizedConfigDisabledAsync(GpuVariant variant, string kvCacheType, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var disabled = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            var key = PairKey(variant, kvCacheType);
            if (!disabled.Contains(key))
            {
                return false;
            }

            if (_dirty.Contains(key) && _repersist.IsCompleted)
            {
                _repersist = Task.Run(RepersistAsync, CancellationToken.None);
            }

            return true;
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
            var key = PairKey(variant, kvCacheType);
            if (!disabled.Add(key) && !_dirty.Contains(key))
            {
                return; // Already recorded and written — idempotent no-op, no re-write.
            }

            _dirty.Add(key);
        }
        finally
        {
            _lock.Release();
        }

        await PersistDirtyAsync(ct).ConfigureAwait(false);
    }

    private static string PairKey(GpuVariant variant, string kvCacheType) =>
        string.Concat(variant.ToString(), ":", (kvCacheType ?? string.Empty).Trim());

    /// <summary>
    ///     Writes a copy of the snapshot under <see cref="_persistLock" /> alone, then merges the disk's verdicts back and
    ///     marks clean only the keys that copy carried.
    /// </summary>
    /// <remarks>A key published while the write ran stays unwritten for the next write; a queued writer finding nothing unwritten returns.</remarks>
    private async Task PersistDirtyAsync(CancellationToken ct)
    {
        await _persistLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            HashSet<string> disabled;
            HashSet<string> snapshot;
            string[] written;
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_dirty.Count == 0 || _disabled is null)
                {
                    return;
                }

                disabled = _disabled;
                snapshot = new HashSet<string>(disabled, StringComparer.OrdinalIgnoreCase);
                written = [.. _dirty];
            }
            finally
            {
                _lock.Release();
            }

            await PersistAsync(snapshot, ct).ConfigureAwait(false);

            await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                disabled.UnionWith(snapshot);
                _dirty.ExceptWith(written);
            }
            finally
            {
                _lock.Release();
            }

            Volatile.Write(ref _repersistFailureLogged, value: 0);
        }
        finally
        {
            _persistLock.Release();
        }
    }

    private async Task RepersistAsync()
    {
        try
        {
            await PersistDirtyAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Warning once per failure streak: every launch reading the verdict retries, and an unwritable cache root would otherwise warn on each.
            if (Interlocked.Exchange(ref _repersistFailureLogged, value: 1) == 0)
            {
                _logger.LogWarning(ex,
                    "Could not re-persist the llama-server launch-fallback verdicts to {StatePath}; they hold in memory and the next launch that reads one retries the write.",
                    _statePath);
            }
        }
    }

    /// <summary>Loads the snapshot once.</summary>
    /// <remarks>Callers already hold <see cref="_lock" />, so the first load's disk read still runs under it.</remarks>
    private async Task<HashSet<string>> EnsureLoadedAsync(CancellationToken ct)
    {
        return _disabled ??= await LoadAsync(ct).ConfigureAwait(false);
    }

    private async Task<HashSet<string>> LoadAsync(CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_statePath))
        {
            return set;
        }

        try
        {
            await using var stream = OpenStateForRead(_statePath);
            var state = await JsonSerializer.DeserializeAsync<LlamaServerLaunchFallbackState>(stream, SerializerOptions, ct).ConfigureAwait(false);
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

        return set;
    }

    /// <summary>
    ///     Opens the state file for reading the way every reader must: shared with writers and deleters, not just with
    ///     other readers.
    /// </summary>
    /// <remarks>
    ///     <see cref="File.OpenRead" /> shares Read only, and on Windows a lock-free sibling read taken that way blocks
    ///     the <c>File.Move(…, overwrite: true)</c> that ends a write — turning a ready safe-retry spawn into a launch
    ///     failure. Readers still see whole documents either way, because the replace is atomic. Internal so the share
    ///     flags can be pinned by a test holding a reader open across a replace.
    /// </remarks>
    internal static FileStream OpenStateForRead(string path) =>
        new(path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete
            });

    /// <summary>
    ///     Replaces the whole state document with <paramref name="disabled" />, folding whatever is already on disk into
    ///     it first.
    /// </summary>
    /// <remarks>
    ///     The file is USER-level, so several node processes share it and each write replaces the whole document:
    ///     without the fold a sibling process's verdict is silently dropped. The re-read and the replace are one
    ///     cross-process critical section held on a sibling lock file rather than on the state file itself, an
    ///     exclusive handle on which would make the atomic <c>File.Move</c> over it fail on Windows; readers take no
    ///     lock at all, so they keep seeing whole documents throughout.
    /// </remarks>
    private async Task PersistAsync(HashSet<string> disabled, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);

        // simplified: a lock this process could not acquire — a sibling holding it past the retry budget, or an unwritable cache root — degrades to the in-process lock
        // alone, the old ceiling, where a sibling write landing inside that window is lost. Accepted: it costs one failed spawn that re-records the verdict.
        await using var crossProcessLock = await AcquireWriteLockOrNullAsync(ct).ConfigureAwait(false);
        disabled.UnionWith(await LoadAsync(ct).ConfigureAwait(false));

        // Every entry is a "{Variant}:{kvType}" pair.
        var state = new LlamaServerLaunchFallbackState([.. disabled]);
        await SecureFilePermissions.WriteJsonAtomicAsync(_statePath, state, SerializerOptions, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Takes the cross-process write lock, or <see langword="null" /> when it could not be had: a sibling still
    ///     holding it after the retry budget, or a lock file this process cannot open for writing.
    /// </summary>
    /// <remarks>
    ///     Either way the write proceeds unlocked, and both paths are logged at Warning — the degraded merge is the one
    ///     thing a silent return would hide.
    /// </remarks>
    private async Task<FileStream?> AcquireWriteLockOrNullAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= LockAttempts; attempt++)
        {
            try
            {
                return SecureFilePermissions.CreateOwnerOnly(_lockPath, FileMode.OpenOrCreate);
            }
            catch (IOException)
            {
                // Held by a sibling node process: back off and retry.
            }
            catch (UnauthorizedAccessException)
            {
                // The lock file itself could not be opened for writing and retrying cannot change that. It says nothing about whether the STATE write below will
                // succeed — that write decides its own writability — so this only reports the degraded merge rather than predicting the outcome of the launch.
                _logger.LogWarning(
                    "Could not open the llama-server launch-fallback write lock at {LockPath} for writing; merging under the in-process lock only, so a concurrent sibling write may be lost.",
                    _lockPath);
                return null;
            }

            if (attempt < LockAttempts)
            {
                await Task.Delay(LockRetryDelay, ct).ConfigureAwait(false);
            }
        }

        _logger.LogWarning(
            "Could not take the llama-server launch-fallback write lock at {LockPath} after {Attempts} attempts; merging under the in-process lock only, so a concurrent sibling write may be lost.",
            _lockPath,
            LockAttempts);
        return null;
    }
}
