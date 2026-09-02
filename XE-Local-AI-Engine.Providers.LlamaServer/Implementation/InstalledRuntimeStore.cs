namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="IInstalledRuntimeStore" />: persists <see cref="InstalledRuntimeState" /> to
///     <c>installed-runtime.json</c> under the cache root.
/// </summary>
/// <remarks>
///     Mirrors the node-settings store: tolerant deserialize (absent/corrupt → <see langword="null" />), atomic write via
///     a temp file then <see cref="File.Move(string, string, bool)" />, and owner-only (0600) permissions on non-Windows.
/// </remarks>
public sealed class InstalledRuntimeStore : IInstalledRuntimeStore, IDisposable
{
    private const string StateFileName = "installed-runtime.json";

    /// <summary>Poll budget for <see cref="AcquireAsync" /> — 400 * 25 ms, so ten seconds of waiting on another node.</summary>
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
        var root = string.IsNullOrWhiteSpace(cacheRoot) ? DefaultCacheRoot() : cacheRoot;
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

        // FileShare.None is a real OS lock on every platform we ship (flock on Unix, a share mode on Windows), and it is
        // released with the handle — including when a process dies — so a leftover lock FILE never wedges anyone. The
        // lock is held only across a read-then-write, which is microseconds, so the wait is a formality; exhausting it
        // means another node is stuck mid-update and overwriting its record blind is the worse outcome.
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

            // Atomic write: serialize to a temp sibling, then move-with-overwrite into place. The temp file is created
            // with owner-only (0600) permissions up front on non-Windows so it is never briefly world-readable; the mode
            // is carried through the same-directory move. Windows relies on the per-user data-directory ACL.
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
            TryDeleteFile(_statePath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     Opens a truncating write stream for <paramref name="path" />. On non-Windows the file is created with
    ///     owner-only (0600) permissions atomically via <see cref="FileStreamOptions.UnixCreateMode" />, mirroring the
    ///     node-settings posture. On Windows <see cref="FileStreamOptions.UnixCreateMode" /> is unsupported, so a plain
    ///     create is used and the per-user data-directory ACL governs access.
    /// </summary>
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
