namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="IInstalledRuntimeStore" />: persists <see cref="InstalledRuntimeState" /> to
///     <c>installed-runtime.json</c> under the cache root.
/// </summary>
/// <remarks>
///     Tolerant deserialize (absent/corrupt → <see langword="null" />); writes go through
///     <see cref="SecureFilePermissions.WriteJsonAtomicAsync{T}" />, atomic and owner-only.
/// </remarks>
public sealed class InstalledRuntimeStore : IInstalledRuntimeStore, IDisposable
{
    private const string StateFileName = "installed-runtime.json";

    /// <summary>Attempts <see cref="AcquireAsync" /> makes, 25 ms apart — just under ten seconds of waiting on another node.</summary>
    private const int LockAttempts = 400;

    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(25);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _lock = new(initialCount: 1, maxCount: 1);
    private readonly string _statePath;

    /// <summary>Creates the store under <paramref name="cacheRoot" /> (defaulting to the shared app cache root).</summary>
    public InstalledRuntimeStore(string? cacheRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(cacheRoot) ? RuntimeCacheDirectory.Resolve() : cacheRoot;
        _statePath = Path.Combine(root, StateFileName);
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    /// <inheritdoc />
    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        var lockPath = _statePath + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);

        // FileShare.None is a real OS lock on every platform we ship (flock on Unix, a share mode on Windows) and is released with the handle, a dying process included, so a
        // leftover lock FILE never wedges anyone. Held only across a read-then-write, so exhausting the wait means another node is stuck mid-update: overwriting it blind is worse.
        for (var attempt = 1; attempt < LockAttempts; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(LockPollInterval, ct).ConfigureAwait(false);
            }
        }

        // Budget spent: the last attempt surfaces its own IOException rather than a synthesized one.
        return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <inheritdoc />
    public async Task<InstalledRuntimeState?> ReadAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_statePath))
            {
                return null;
            }

            try
            {
                await using var stream = File.OpenRead(_statePath);
                return await JsonSerializer.DeserializeAsync<InstalledRuntimeState>(stream, SerializerOptions, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // Corrupt state file → treat as no installed state; resolution falls to the pinned floor.
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(InstalledRuntimeState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            await SecureFilePermissions.WriteJsonAtomicAsync(_statePath, state, SerializerOptions, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            BestEffortFile.TryDeleteFile(_statePath);
        }
        finally
        {
            _lock.Release();
        }
    }
}
