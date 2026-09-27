namespace XE_Local_AI_Engine.Providers.Python;

using System.Runtime.Versioning;
using Microsoft.Win32;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The one machine-global toolchain store every uv-managed feature provisions against: the pinned uv, the managed
///     CPython installs and the uv cache. Feature environments stay in their own roots.
/// </summary>
/// <remarks>
///     Nothing here ever deletes under <see cref="Root" />: several hosts and checkouts share it, and a venv's
///     <c>pyvenv.cfg</c> <c>home</c> points into it, so no host can know what another still uses (ADR 0016 §3).
///     Concurrent writers are safe by construction: uv locks its cache and install dir, and the uv extract loses a race
///     gracefully (<c>UvBinaryAcquirer</c>).
/// </remarks>
public sealed class ManagedPythonToolchain
{
    // MAX_PATH is 260; CPython's deepest stdlib files and a venv's wheel payloads (its root is a sibling of this one) run
    // 100-150 characters below their roots, so a store root past 100 leaves no margin under MAX_PATH.
    internal const int WindowsStoreRootBudget = 100;

    // How often a caller queued behind another holder of an exclusive lock file retries it.
    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The directory names a pre-shared-store feature root kept its own toolchain under.</summary>
    private static readonly string[] LegacyDirectoryNames = ["uv", "pythons", "uv-cache"];

    /// <param name="root">The store root; tests pin a temp directory, production uses <see cref="Default" />.</param>
    public ManagedPythonToolchain(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = root;
    }

    /// <summary>The store root; <c>UvBinaryAcquirer.EnsureUvAsync</c> keys the uv download under <c>uv/&lt;version&gt;</c> below it.</summary>
    public string Root { get; }

    /// <summary><c>UV_PYTHON_INSTALL_DIR</c>: the managed CPython installs, and the tree a sandboxed interpreter must bind.</summary>
    public string PythonInstallDirectory => Path.Combine(Root, "pythons");

    /// <summary><c>UV_CACHE_DIR</c>.</summary>
    public string CacheDirectory => Path.Combine(Root, "cache");

    /// <summary>Where <c>UvBinaryAcquirer.EnsureUvAsync</c> lands the pinned uv executable in this store.</summary>
    /// <exception cref="ManagedPythonException">No uv asset is pinned for this platform.</exception>
    public string PinnedUvExecutable => ManagedPythonPins.Current.ExecutablePath(Root);

    /// <summary>
    ///     The managed CPython installs present in the store, by directory name (<c>cpython-3.13.15-linux-x86_64-gnu</c>).
    ///     Reads the directory only: uv's minor-version alias symlinks and its dot-prefixed lock and temp entries are skipped.
    /// </summary>
    public IReadOnlyList<string> ListPythonInstalls()
    {
        if (!Directory.Exists(PythonInstallDirectory))
        {
            return [];
        }

        return new DirectoryInfo(PythonInstallDirectory).EnumerateDirectories()
                                                        .Where(static directory => directory.LinkTarget is null && !directory.Name.StartsWith('.'))
                                                        .Select(static directory => directory.Name)
                                                        .Order(StringComparer.Ordinal)
                                                        .ToArray();
    }

    /// <summary>A venv's interpreter: <c>bin/python</c> on Unix, <c>Scripts\python.exe</c> on Windows.</summary>
    /// <param name="venvRoot">The venv directory itself (the one holding <c>pyvenv.cfg</c>).</param>
    public static string VenvInterpreterPath(string venvRoot)
    {
        return VenvInterpreterPath(venvRoot, OperatingSystem.IsWindows());
    }

    internal static string VenvInterpreterPath(string venvRoot, bool isWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venvRoot);
        return isWindows ? Path.Combine(venvRoot, "Scripts", "python.exe") : Path.Combine(venvRoot, "bin", "python");
    }

    /// <summary>
    ///     Refuses, on Windows without long-path support, a store root too long for uv and CPython to stay under MAX_PATH.
    /// </summary>
    /// <remarks>
    ///     .NET handles long paths natively, but uv, CPython and wheel payloads may not. Called by the uv acquisition every
    ///     provision of every feature starts with. The registry read is Windows-only; <see cref="ExceedsWindowsPathBudget" />
    ///     is the pure decision.
    /// </remarks>
    public static void EnsureWindowsPathBudget(string storeRoot)
    {
        if (OperatingSystem.IsWindows() && ExceedsWindowsPathBudget(storeRoot, isWindows: true, WindowsLongPathsEnabled()))
        {
            throw new ManagedPythonException("The Python toolchain folder path is too long for Windows without long-path support. Enable long paths in Windows or use a shorter data directory.");
        }
    }

    internal static bool ExceedsWindowsPathBudget(string storeRoot, bool isWindows, bool longPathsEnabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        return isWindows && !longPathsEnabled && Path.GetFullPath(storeRoot).Length > WindowsStoreRootBudget;
    }

    public static ManagedPythonToolchain Default()
    {
        return new ManagedPythonToolchain(Path.Combine(RuntimeCacheDirectory.Resolve(), "python"));
    }

    /// <summary>True when a feature root still holds any of its pre-shared-store <c>uv</c>, <c>pythons</c> or <c>uv-cache</c>.</summary>
    public static bool HasLegacyToolchain(string featureRoot)
    {
        return LegacyDirectoryNames.Any(name => Directory.Exists(Path.Combine(featureRoot, name)));
    }

    /// <summary>
    ///     Deletes a feature root's legacy <c>uv</c>, <c>pythons</c> and <c>uv-cache</c>, best-effort; false when any
    ///     of them is still on disk afterwards.
    /// </summary>
    /// <remarks>
    ///     The caller must already know no environment of that feature points into them. A tree that refuses the delete
    ///     gets its directory write bits restored once and is retried: unlinking needs write permission on the parent.
    /// </remarks>
    public static bool TryDeleteLegacyToolchain(string featureRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureRoot);
        var removed = true;
        foreach (var name in LegacyDirectoryNames)
        {
            removed &= TryDeleteTree(Path.Combine(featureRoot, name));
        }

        return removed;
    }

    /// <summary>
    ///     Takes an exclusive, cross-process lock on <paramref name="path" />: <c>FileShare.None</c> is an exclusive
    ///     <c>flock(2)</c> on Unix, released when the returned handle is disposed or the process dies.
    /// </summary>
    /// <remarks>
    ///     A lock file must never be deleted while hosts run: a new inode at the same path would hand out a second
    ///     holder. Only the uninstaller removes it. A wait past <paramref name="maxWait" /> throws a user-safe
    ///     <see cref="ManagedPythonException" />; a caller cancellation propagates as such.
    /// </remarks>
    public static async Task<FileStream> AcquireExclusiveLockAsync(string path, TimeSpan maxWait, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(maxWait);
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                try
                {
                    await Task.Delay(LockPollInterval, bounded.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new ManagedPythonException("Another process kept the Python toolchain busy for too long. Try again later.");
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool WindowsLongPathsEnabled()
    {
        return Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 0) is 1;
    }

    private static bool TryDeleteTree(string path)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == 0)
                {
                    RestoreDirectoryWriteBits(path);
                }
            }
        }

        return false;
    }

    private static void RestoreDirectoryWriteBits(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var directories = new DirectoryInfo(root).EnumerateDirectories("*", SearchOption.AllDirectories)
                                                     .Append(new DirectoryInfo(root))
                                                     .Where(static directory => directory.LinkTarget is null);
            foreach (var directory in directories)
            {
                directory.UnixFileMode |= UnixFileMode.UserWrite;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The retry reports the failure; a tree that cannot even be walked stays on disk.
        }
    }
}
