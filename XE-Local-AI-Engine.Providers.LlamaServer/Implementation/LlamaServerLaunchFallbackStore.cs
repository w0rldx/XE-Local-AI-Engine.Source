namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="ILlamaServerLaunchFallbackStore" />: persists the set of GPU backends whose optimized launch
///     config (quantized KV cache + flash attention) has proven unable to reach readiness on this host to
///     <c>llama-launch-fallback.json</c> under the cache root.
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

    // Null until first load; then the authoritative in-memory snapshot of disabled variants (case-insensitive by name).
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
    public async Task<bool> IsOptimizedConfigDisabledAsync(GpuVariant variant, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var disabled = _disabled ??= await LoadAsync(ct).ConfigureAwait(false);
            return disabled.Contains(variant.ToString());
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DisableOptimizedConfigAsync(GpuVariant variant, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var disabled = _disabled ??= await LoadAsync(ct).ConfigureAwait(false);
            if (!disabled.Add(variant.ToString()))
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
                foreach (var name in variants.Where(static name => !string.IsNullOrWhiteSpace(name)))
                {
                    set.Add(name);
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

        var state = new LlamaServerLaunchFallbackState([.. disabled]);
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

/// <summary>Persisted shape for <see cref="LlamaServerLaunchFallbackStore" />: the GPU backends whose optimized launch config is disabled.</summary>
/// <param name="DisabledOptimizedVariants">Backend names (<see cref="GpuVariant" />) whose KV-quant + flash-attention config failed readiness.</param>
public sealed record LlamaServerLaunchFallbackState(IReadOnlyList<string> DisabledOptimizedVariants);
