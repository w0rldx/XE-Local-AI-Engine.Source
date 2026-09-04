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
    // (case-insensitive). A legacy backend-only entry is kept as the bare "{Variant}" and disables every KV type on it.
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
            var disabled = _disabled ??= await LoadAsync(ct).ConfigureAwait(false);

            // A legacy entry names the backend alone. It was written when one verdict covered every KV type, so it must
            // keep covering every KV type — reading it as "only the current type" would silently re-enable a config this
            // host already proved cannot reach readiness.
            return disabled.Contains(variant.ToString()) || disabled.Contains(PairKey(variant, kvCacheType));
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
            var disabled = _disabled ??= await LoadAsync(ct).ConfigureAwait(false);
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

    private async Task<HashSet<string>> LoadAsync(CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_statePath))
        {
            return set;
        }

        try
        {
            await using var stream = File.OpenRead(_statePath);
            var state = await JsonSerializer.DeserializeAsync<LlamaServerLaunchFallbackState>(stream, SerializerOptions, ct).ConfigureAwait(false);
            if (state?.DisabledOptimizedVariants is { } variants)
            {
                // Legacy, backend-only entries. Read for back-compat and never written again; each disables every KV
                // type on its backend, which preserves the pre-slice behaviour across an upgrade.
                foreach (var name in variants.Where(static name => !string.IsNullOrWhiteSpace(name)))
                {
                    set.Add(name);
                }
            }

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

    private async Task PersistAsync(HashSet<string> disabled, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);

        // Legacy backend-only entries are preserved verbatim in their own list so an older build still reads them; every
        // new entry is a "{Variant}:{kvType}" pair and goes only into the pair list.
        var legacy = disabled.Where(static key => !key.Contains(':', StringComparison.Ordinal)).ToArray();
        var pairs = disabled.Where(static key => key.Contains(':', StringComparison.Ordinal)).ToArray();
        var state = new LlamaServerLaunchFallbackState(legacy, pairs);
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
///     LEGACY, read-only: backend names (<see cref="GpuVariant" />) recorded before the store was keyed by KV type. An
///     entry here disables every KV type on that backend. Nothing writes to this list any more.
/// </param>
/// <param name="DisabledOptimizedConfigs">
///     <c>"{Variant}:{kvType}"</c> keys whose KV-quant + flash-attention config failed readiness. A <c>q4_0</c> failure
///     here leaves <c>q8_0</c> on the same backend enabled.
/// </param>
public sealed record LlamaServerLaunchFallbackState(IReadOnlyList<string> DisabledOptimizedVariants,
    IReadOnlyList<string>? DisabledOptimizedConfigs = null);
