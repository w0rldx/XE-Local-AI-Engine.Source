namespace XE_Local_AI_Engine.Client.Services.Compute.Implementation;

using System.Security.Cryptography;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.ManagedPython;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Python;

/// <summary>
///     The compute runtime's on-disk layout: its paths, the installed-state record, the environment identity and the tree
///     modes. Holds no in-process state; the gate, lease and cached runtime are <see cref="ComputePythonEnvironment" />'s.
/// </summary>
internal sealed class ComputeRuntimeDirectory
{
    internal const string ProjectFileName = "pyproject.toml";
    internal const string LockfileName = "uv.lock";
    private const string StateFileName = "installed-compute-runtime.json";
    private const string LegacyStateFileName = "installed-compute-lock.sha256";
    private const string ProvisionLockFileName = ".provision.lock";

    /// <summary>Bump when the environment's shape changes without its lockfile changing; a mismatch rebuilds the venv.</summary>
    private const int ProfileRevision = 1;

    /// <summary>
    ///     The script scratch directory of the PRE-JAIL layout.
    /// </summary>
    /// <remarks>
    ///     It sat beside the venv under the compute cache root, which is space the jail-occupancy watchdog never walked
    ///     and which one call could read out of the next. Both holes are closed — the scratch is inside the
    ///     per-invocation jail — but a machine that ran an older build still has the directory, with whatever those calls
    ///     left in it, so it is swept once before the tool can run.
    /// </remarks>
    private const string LegacyScratchDirectoryName = "scratch";

    /// <summary>The name the shipped compute project files are linked under in the publish output (see the Client csproj).</summary>
    private const string PublishedScriptsDirectoryName = "compute-scripts";

    /// <summary>The repo-relative source of the same files, used by dev and test runs.</summary>
    private const string RepositoryScriptsRelativePath = "tools/compute";

    private readonly string _scriptsDirectory;
    private readonly ManagedPythonToolchain _toolchain;

    public ComputeRuntimeDirectory(string cacheRoot, string scriptsDirectory, ManagedPythonToolchain toolchain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptsDirectory);
        CacheRoot = cacheRoot;
        _scriptsDirectory = scriptsDirectory;
        _toolchain = toolchain ?? throw new ArgumentNullException(nameof(toolchain));
    }

    public string CacheRoot { get; }

    /// <summary>The uv project directory: the committed <c>pyproject.toml</c> + <c>uv.lock</c> and the <c>.venv</c> beside them.</summary>
    public string VenvDirectory => Path.Combine(CacheRoot, "venv");

    public string VenvRoot => Path.Combine(VenvDirectory, ".venv");

    /// <summary>Where a rebuild syncs, so the live venv stays in service until the new one is proven.</summary>
    public string StagingDirectory => Path.Combine(CacheRoot, "venv.staging");

    /// <summary>Where the live venv is parked for the instant between the swap's two renames.</summary>
    public string BackupDirectory => Path.Combine(CacheRoot, "venv.backup");

    public string InterpreterPath => ManagedPythonToolchain.VenvInterpreterPath(VenvRoot);

    public string WorkDirectory => Path.Combine(CacheRoot, ".work");

    public string ProvisionLockPath => Path.Combine(CacheRoot, ProvisionLockFileName);

    public string LegacyStatePath => Path.Combine(CacheRoot, LegacyStateFileName);

    public string LegacyScratchDirectory => Path.Combine(CacheRoot, LegacyScratchDirectoryName);

    public string ProjectPath => Path.Combine(_scriptsDirectory, ProjectFileName);

    public string LockfilePath => Path.Combine(_scriptsDirectory, LockfileName);

    public string StatePath => Path.Combine(CacheRoot, StateFileName);

    private string TemporaryStatePath => StatePath + ".tmp";

    /// <summary>
    ///     A non-blocking probe of <c>.provision.lock</c>: never creates it and releases it at once. Only called with the
    ///     in-process gate free, so a holder is another instance or host.
    /// </summary>
    public async Task<bool> IsProvisionLockHeldElsewhereAsync()
    {
        try
        {
            await using var probe = new FileStream(ProvisionLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<ManagedPythonEnvironmentIdentity> ComputeExpectedIdentityAsync(CancellationToken cancellationToken)
    {
        return BuildIdentity(await ComputeFileShaAsync(LockfilePath, cancellationToken));
    }

    /// <summary>The identity the shipped lockfile asks for, or null when it cannot be read.</summary>
    public async Task<ManagedPythonEnvironmentIdentity?> TryComputeExpectedIdentityAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ComputeExpectedIdentityAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The identity mismatches, plus <c>toolchainStore</c> when the venv was built against another CPython root, which it keeps pointing into.</summary>
    public List<string> MismatchesAgainst(InstalledState installed, ManagedPythonEnvironmentIdentity expected)
    {
        var mismatches = installed.Identity.MismatchesAgainst(expected).ToList();
        if (!string.Equals(installed.PythonInstallDirectory, _toolchain.PythonInstallDirectory, StringComparison.Ordinal))
        {
            mismatches.Add("toolchainStore");
        }

        return mismatches;
    }

    public async Task<InstalledState?> ReadInstalledStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return null;
            }

            await using var stream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<InstalledState>(stream, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable record is treated as absent: re-syncing an already-correct venv is cheap and idempotent,
            // whereas trusting a record we could not read would serve a closure nothing verified.
            return null;
        }
    }

    /// <summary>Drops leftovers from an interrupted rebuild. The caller holds the provision lock.</summary>
    /// <remarks>
    ///     A staging tree and a half-written record are by definition unadopted, so both go. A parked venv beside a live one is a finished swap whose
    ///     cleanup did not run; a parked venv WITHOUT a live one is a swap that died between its two renames, so it is put
    ///     back, and its record, which the swap had not yet replaced, describes it again.
    /// </remarks>
    public void Recover()
    {
        TryDeleteTree(StagingDirectory);
        File.Delete(TemporaryStatePath);
        if (!Directory.Exists(BackupDirectory))
        {
            return;
        }

        if (Directory.Exists(VenvDirectory))
        {
            TryDeleteTree(BackupDirectory);
            return;
        }

        Directory.Move(BackupDirectory, VenvDirectory);
    }

    /// <summary>
    ///     Adopts the verified staging tree: parks the live venv, renames staging in, replaces the record, drops the parked venv.
    ///     It takes no token, so no cancellation lands between the renames. The caller holds the provision lock.
    /// </summary>
    /// <remarks>
    ///     A failure before the record lands renames both trees back; the old record was never touched, because the write
    ///     replaces it atomically. A crash between the renames is <see cref="Recover" />'s. Renames within one parent keep
    ///     the venv runnable: <c>pyvenv.cfg</c> names the store by absolute path, and <c>sys.prefix</c> follows the interpreter.
    /// </remarks>
    public async Task SwapAsync(InstalledState state)
    {
        var parked = false;
        var swapped = false;
        try
        {
            if (Directory.Exists(VenvDirectory))
            {
                Directory.Move(VenvDirectory, BackupDirectory);
                parked = true;
            }

            Directory.Move(StagingDirectory, VenvDirectory);
            swapped = true;
            await WriteInstalledStateAsync(state);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (swapped)
            {
                Directory.Move(VenvDirectory, StagingDirectory);
            }

            if (parked)
            {
                Directory.Move(BackupDirectory, VenvDirectory);
            }

            throw;
        }

        TryDeleteTree(BackupDirectory);
    }

    /// <summary>Deletes the live, staging and parked venv trees; throws when one cannot go.</summary>
    public void DeleteVenvTrees()
    {
        string[] trees = [VenvDirectory, StagingDirectory, BackupDirectory];
        foreach (var tree in trees)
        {
            SetTreeWritable(tree, writable: true);
            if (Directory.Exists(tree))
            {
                Directory.Delete(tree, recursive: true);
            }
        }
    }

    /// <summary>Best-effort: a tree a previous provision locked down needs its write bits back before it can be deleted.</summary>
    public static void TryDeleteTree(string path)
    {
        SetTreeWritable(path, writable: true);
        TryDeleteDirectory(path);
    }

    private async Task WriteInstalledStateAsync(InstalledState state)
    {
        var temporary = TemporaryStatePath;
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(state), CancellationToken.None);
        File.Move(temporary, StatePath, overwrite: true);
    }

    /// <summary>
    ///     Clears (or restores) the write bits across the venv tree.
    /// </summary>
    /// <remarks>
    ///     Scripts reach the interpreter through <c>sys.executable</c>, and a writable <c>site-packages</c> lets one call drop a module
    ///     every later approved call imports — a single approval turned into persistent code execution. <b>This is defence in depth and no
    ///     longer the boundary:</b> the boundary is the read-only bind mount under
    ///     <see cref="XE_Local_AI_Engine.Client.Services.Sandbox.SandboxIsolationMode.Filesystem" />, where an <c>os.chmod</c> and a write
    ///     both answer <c>EROFS</c>. The mode bits still cover OUTSIDE that namespace: the engine's own processes, an operator's shell.
    /// </remarks>
    public static void SetTreeWritable(string root, bool writable)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(root))
        {
            return;
        }

        const UnixFileMode WriteBits = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        var rootInfo = new DirectoryInfo(root);
        foreach (var entry in rootInfo.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Append(rootInfo))
        {
            try
            {
                // chmod follows a symlink, and bin/python links into the shared CPython store, which is not this tree's to lock.
                if (entry.LinkTarget is not null)
                {
                    continue;
                }

                var mode = entry.UnixFileMode;
                entry.UnixFileMode = writable ? mode | UnixFileMode.UserWrite : mode & ~WriteBits;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A dangling symlink or a file removed under the walk is not worth failing a provision over.
            }
        }
    }

    public static void CreateOwnerOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Inherits the per-user %LOCALAPPDATA% ACL; no ACL code of its own (ADR 0016).
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort sweep of the provision scratch directory.
        }
    }

    /// <summary>
    ///     The machine-global compute cache root, under the same base the llama.cpp binaries and the training runtime
    ///     use so one provision serves every node profile on the host and the existing uninstaller sweep already reaches it.
    /// </summary>
    public static string DefaultCacheRoot()
    {
        return Path.Combine(RuntimeCacheDirectory.Resolve(),
            "compute-runtime");
    }

    /// <summary>
    ///     Resolves the directory holding <c>pyproject.toml</c> / <c>uv.lock</c>.
    /// </summary>
    /// <remarks>
    ///     The published app carries them beside the executable and a dev or test run reads them out of the working
    ///     tree, which is why the repo path is a fallback rather than the only answer: the repo root is outside the
    ///     publish glob and does not exist in a shipped install. Mirrors
    ///     <c>TrainingRuntimeLayout.ResolveScriptsDirectory</c>.
    /// </remarks>
    public static string ResolveScriptsDirectory()
    {
        var published = Path.Combine(AppContext.BaseDirectory, PublishedScriptsDirectoryName);
        if (File.Exists(Path.Combine(published, LockfileName)))
        {
            return published;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, RepositoryScriptsRelativePath);
            if (File.Exists(Path.Combine(candidate, LockfileName)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        // Nothing found: return the published path so the missing-lockfile refusal names the location a shipped install
        // would actually use, rather than inventing one.
        return published;
    }

    private static ManagedPythonEnvironmentIdentity BuildIdentity(string lockfileSha256)
    {
        return new ManagedPythonEnvironmentIdentity
        {
            ProfileId = ComputePythonEnvironment.ProfileId,
            PythonMinor = ManagedPythonPins.PythonMinor,
            LockfileSha256 = lockfileSha256,
            ProfileRevision = ProfileRevision,
            Rid = ManagedPythonPins.Current.Rid,
            UvVersion = ManagedPythonPins.UvVersion
        };
    }

    private static async Task<string> ComputeFileShaAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    /// <summary>The persisted record: the identity plus the store the venv links into.</summary>
    internal sealed class InstalledState
    {
        public required ManagedPythonEnvironmentIdentity Identity { get; init; }

        public required string PythonInstallDirectory { get; init; }
    }
}
