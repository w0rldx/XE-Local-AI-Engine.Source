namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Owns the journaled directory-and-state transaction that turns a finished build tree into a runtime's managed
///     source-built runtime.
/// </summary>
/// <remarks>
///     Journal-first: the intent is written to disk before any directory moves, so a host that dies mid-adoption is reconciled on the
///     next start rather than left with a record and a tree that disagree, and <see cref="RecoverAsync" /> is that reconciliation.
///     <b>Link policy:</b> a build's output legitimately contains SONAME symlink chains, so links can be neither rejected nor blindly
///     accepted — hardening walks the tree setting permissions, and a link escaping the staging root would chmod a file outside. See
///     docs/wiki/24-audio-transcription.md ("Adopting a managed source build").
/// </remarks>
public abstract class ManagedRuntimeAdoption<TBackend>
    where TBackend : struct, Enum
{
    private readonly string _cacheRoot;
    private readonly string _runtimeName;
    private readonly string _runtimeLabel;
    private readonly string _serverName;
    private readonly IManagedRuntimeStore<TBackend> _runtimeStore;
    private readonly IManagedSourceBuildSignal<TBackend> _managedSignal;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <param name="runtimeName">The runtime's cache directory and log-line name, such as <c>whisper.cpp</c>.</param>
    /// <param name="runtimeLabel">The runtime as operator-facing messages name it, such as <c>whisper.cpp runtime</c>.</param>
    /// <param name="serverName">The server executable's file name without extension, such as <c>whisper-server</c>.</param>
    protected ManagedRuntimeAdoption(string cacheRoot,
        string runtimeName,
        string runtimeLabel,
        string serverName,
        IManagedRuntimeStore<TBackend> runtimeStore,
        IManagedSourceBuildSignal<TBackend> managedSignal,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _cacheRoot = cacheRoot;
        _runtimeName = runtimeName;
        _runtimeLabel = runtimeLabel;
        _serverName = serverName;
        _runtimeStore = runtimeStore;
        _managedSignal = managedSignal;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private string BuildRoot => Path.Combine(_cacheRoot, _runtimeName, "source-build");
    private string JournalPath => Path.Combine(BuildRoot, "adoption-journal.json");
    private string RuntimeRoot => Path.Combine(_cacheRoot, _runtimeName, "managed");

    /// <summary>The directory name a backend's managed builds live under.</summary>
    protected abstract string BackendSlug(TBackend backend);

    /// <summary>The runtime's own display-safe exception carrying <paramref name="message" />.</summary>
    protected abstract Exception RuntimeError(string message, Exception? innerException = null);

    /// <summary>
    ///     Reconciles a journal left behind by an interrupted adoption: completes the cleanup when the new runtime is
    ///     already committed, keeps an untouched previous one, and otherwise rolls back.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        if (!File.Exists(JournalPath))
        {
            return;
        }

        ManagedRuntimeAdoptionJournal<TBackend> journal;
        try
        {
            await using var stream = File.OpenRead(JournalPath);
            journal = await JsonSerializer.DeserializeAsync<ManagedRuntimeAdoptionJournal<TBackend>>(stream, cancellationToken: ct).ConfigureAwait(false)
                      ?? throw RuntimeError($"The managed {_runtimeLabel} adoption journal is invalid.");
        }
        catch (JsonException exception)
        {
            throw RuntimeError($"The managed {_runtimeLabel} adoption journal is invalid.", exception);
        }

        var paths = GetPaths(journal);
        var installed = await _runtimeStore.ReadAsync(ct).ConfigureAwait(false);

        // The adoption had committed: the record and the tree both describe the new runtime, so only the cleanup was
        // left unfinished.
        if (installed is not null && RuntimeStatesMatch(installed, journal.NewState) && Directory.Exists(paths.Destination))
        {
            try
            {
                CleanupCommitted(paths);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "A committed {RuntimeName} runtime cleanup remains pending and will be retried.", _runtimeName);
            }

            return;
        }

        // The swap had not started: the destination still holds the PREVIOUS runtime and there is no backup to restore because none was ever taken. Proven on bytes, not on the journal's word, before
        // the journal is dropped — otherwise a half-written destination would be adopted as healthy.
        if (journal.HadPreviousDestination
            && !Directory.Exists(paths.Backup)
            && Directory.Exists(paths.Destination)
            && journal.PreviousState is not null
            && installed is not null
            && RuntimeStatesMatch(installed, journal.PreviousState))
        {
            if (await ManagedRuntimeBytesMatchStateAsync(journal.PreviousState, paths.Destination, ct).ConfigureAwait(false))
            {
                DeleteDirectoryStrict(paths.Failed);
                DeleteFileStrict(JournalPath);
                return;
            }

            throw RuntimeError($"The previous managed {_runtimeLabel} backup is missing and the installed bytes cannot be safely identified.");
        }

        await RollbackAsync(journal, paths).ConfigureAwait(false);
    }

    /// <summary>
    ///     Moves a verified build tree into the managed install root and publishes the record that makes it the node's
    ///     runtime, rolling back to the previous runtime if any step fails.
    /// </summary>
    /// <param name="pending">
    ///     The record to publish; the adoption fills in its build path, server digest and install time.
    /// </param>
    protected async Task AdoptCoreAsync(string buildDir,
        string serverPath,
        Guid buildId,
        ManagedRuntimeState<TBackend> pending,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pending);

        var relativeServer = Path.GetRelativePath(buildDir, serverPath);
        if (relativeServer.StartsWith("..", StringComparison.Ordinal))
        {
            throw RuntimeError($"The built {_serverName} path escaped the build directory.");
        }

        var commit = pending.SourceCommit;
        var backendRoot = Path.Combine(RuntimeRoot, BackendSlug(pending.DesiredBackend));
        var destination = Path.Combine(backendRoot, commit);
        var staging = Path.Combine(backendRoot, $".staging-{commit}-{buildId:N}");
        var backup = Path.Combine(backendRoot, $".backup-{commit}-{buildId:N}");
        var failed = Path.Combine(backendRoot, $".failed-{commit}-{buildId:N}");
        SourceProcessHardening.CreateOwnerOnlyDirectory(RuntimeRoot);
        SourceProcessHardening.CreateOwnerOnlyDirectory(backendRoot);
        BestEffortFile.TryDeleteDirectory(staging);
        BestEffortFile.TryDeleteDirectory(backup);
        BestEffortFile.TryDeleteDirectory(failed);
        Directory.Move(buildDir, staging);
        try
        {
            // Order is load-bearing: link validation, THEN hardening, THEN the swap. Hardening first would chmod
            // through an escaping link before anything had judged it.
            PrepareStagingTree(staging);
            var stagedServer = Path.GetFullPath(Path.Combine(staging, relativeServer));
            ValidateAdoptedServer(staging, stagedServer);
            var digest = await FileSha256.ComputeAsync(stagedServer, ct).ConfigureAwait(false);
            var finalServer = Path.GetFullPath(Path.Combine(destination, relativeServer));
            var state = pending with
            {
                Validity = ManagedRuntimeValidity.Active,
                SourceBuildPath = Path.GetDirectoryName(finalServer),
                ServerSha256 = digest,
                InstalledAtUtc = _timeProvider.GetUtcNow(),
                InvalidReason = null
            };
            var previousState = await _runtimeStore.ReadAsync(ct).ConfigureAwait(false);
            var journal = new ManagedRuntimeAdoptionJournal<TBackend>(buildId,
                state.DesiredBackend,
                commit,
                Directory.Exists(destination),
                previousState,
                state);
            var paths = GetPaths(journal);
            await WriteJournalAsync(journal, ct).ConfigureAwait(false);

            try
            {
                if (journal.HadPreviousDestination)
                {
                    Directory.Move(destination, backup);
                }

                Directory.Move(staging, destination);

                // Re-validated at the FINAL path: the move is what the runtime will actually spawn from, and a
                // permission or link property that held in staging is worth proving where it matters.
                ValidateAdoptedServer(destination, finalServer);
                await _runtimeStore.WriteAsync(state, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception adoptionException)
            {
                try
                {
                    await RollbackAsync(journal, paths).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    _managedSignal.Clear();
                    throw RuntimeError($"The managed {_runtimeLabel} adoption failed and its previous state could not be restored.",
                        new AggregateException(adoptionException, rollbackException));
                }

                ExceptionDispatchInfo.Capture(adoptionException).Throw();
                throw;
            }

            _managedSignal.SetActive(state.DesiredBackend);
            try
            {
                CleanupCommitted(paths);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "The previous {RuntimeName} runtime cleanup is pending and will be retried.", _runtimeName);
            }
        }
        catch
        {
            BestEffortFile.TryDeleteDirectory(staging);
            throw;
        }
    }

    /// <summary>
    ///     Validates every link in the staging tree and then hardens the real entries it found. One walk does both,
    ///     which is what guarantees hardening can only ever touch entries the validation already judged.
    /// </summary>
    private void PrepareStagingTree(string staging)
    {
        var root = Path.GetFullPath(staging);
        List<string> directories = [root];
        List<string> files = [];
        WalkValidatingLinks(root, root, directories, files);

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var directory in directories)
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        foreach (var file in files)
        {
            var existing = File.GetUnixFileMode(file);
            var execute = existing & UnixFileMode.UserExecute;
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | execute);
        }
    }

    // Deliberately hand-rolled rather than SearchOption.AllDirectories: recursion must stop at a link instead of descending through one, or a directory link pointing outside the tree would be walked
    // — and hardened — before anything judged it.
    private void WalkValidatingLinks(string root, string directory, List<string> directories, List<string> files)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            // FileSystemInfo.LinkTarget reports the entry's own link, whatever the target's kind, so this recognizes a
            // directory link without following it.
            var linkTarget = new FileInfo(entry).LinkTarget;
            if (linkTarget is not null)
            {
                ValidateLink(root, entry, linkTarget);
                continue;
            }

            if (Directory.Exists(entry))
            {
                directories.Add(entry);
                WalkValidatingLinks(root, entry, directories, files);
            }
            else
            {
                files.Add(entry);
            }
        }
    }

    // Each hop of a SONAME chain is judged on its own: a chain is safe exactly when every link in it is relative and
    // lands inside the tree, which is cheaper and stricter than resolving a final target that need not exist yet.
    private void ValidateLink(string root, string linkPath, string linkTarget)
    {
        if (Path.IsPathRooted(linkTarget))
        {
            throw RuntimeError("The source build produced a runtime containing an unsafe link.");
        }

        var containingDirectory = Path.GetDirectoryName(Path.GetFullPath(linkPath))
                                  ?? throw RuntimeError("The source build produced a runtime containing an unsafe link.");
        var resolved = Path.GetFullPath(Path.Combine(containingDirectory, linkTarget));
        var rootPrefix = root + Path.DirectorySeparatorChar;
        if (!string.Equals(resolved, root, StringComparison.Ordinal) && !resolved.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw RuntimeError("The source build produced a runtime containing an unsafe link.");
        }
    }

    private async Task RollbackAsync(ManagedRuntimeAdoptionJournal<TBackend> journal, AdoptionPaths paths)
    {
        _managedSignal.Clear();
        if (Directory.Exists(paths.Destination))
        {
            DeleteDirectoryStrict(paths.Failed);
            Directory.Move(paths.Destination, paths.Failed);
        }

        if (journal.HadPreviousDestination)
        {
            if (!Directory.Exists(paths.Backup))
            {
                throw RuntimeError($"The previous managed {_runtimeLabel} backup is missing.");
            }

            Directory.Move(paths.Backup, paths.Destination);
        }

        if (paths.RetiredPrevious is not null && Directory.Exists(paths.RetiredPrevious))
        {
            if (paths.PreviousInstallRoot is null || Directory.Exists(paths.PreviousInstallRoot))
            {
                throw RuntimeError($"The previous managed {_runtimeLabel} could not be recovered.");
            }

            Directory.Move(paths.RetiredPrevious, paths.PreviousInstallRoot);
        }

        await RestorePreviousStateAsync(journal.PreviousState).ConfigureAwait(false);
        DeleteDirectoryStrict(paths.Failed);
        DeleteFileStrict(JournalPath);
    }

    private async Task WriteJournalAsync(ManagedRuntimeAdoptionJournal<TBackend> journal, CancellationToken ct)
    {
        SourceProcessHardening.CreateOwnerOnlyDirectory(BuildRoot);
        var temporaryPath = JournalPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 4096,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, journal, cancellationToken: ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        File.Move(temporaryPath, JournalPath, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(JournalPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private AdoptionPaths GetPaths(ManagedRuntimeAdoptionJournal<TBackend> journal)
    {
        if (journal.NewCommit.Length != 40
            || !journal.NewCommit.All(Uri.IsHexDigit)
            || journal.NewState.SourceCommit != journal.NewCommit
            || !EqualityComparer<TBackend>.Default.Equals(journal.NewState.DesiredBackend, journal.NewBackend))
        {
            throw RuntimeError($"The managed {_runtimeLabel} adoption journal is invalid.");
        }

        var backendRoot = Path.Combine(RuntimeRoot, BackendSlug(journal.NewBackend));
        var destination = Path.Combine(backendRoot, journal.NewCommit);
        var backup = Path.Combine(backendRoot, $".backup-{journal.NewCommit}-{journal.BuildId:N}");
        var failed = Path.Combine(backendRoot, $".failed-{journal.NewCommit}-{journal.BuildId:N}");
        string? previousInstallRoot = null;
        string? retiredPrevious = null;
        if (journal.PreviousState is not null)
        {
            previousInstallRoot = GetManagedInstallRoot(journal.PreviousState);
            if (!PathContainment.PathsEqual(previousInstallRoot, destination))
            {
                retiredPrevious = Path.Combine(Path.GetDirectoryName(previousInstallRoot)!,
                    $".retired-{journal.PreviousState.SourceCommit}-{journal.BuildId:N}");
            }
        }

        return new AdoptionPaths
        {
            Destination = destination,
            Backup = backup,
            Failed = failed,
            PreviousInstallRoot = previousInstallRoot,
            RetiredPrevious = retiredPrevious
        };
    }

    private void CleanupCommitted(AdoptionPaths paths)
    {
        if (paths.RetiredPrevious is not null && paths.PreviousInstallRoot is not null)
        {
            if (Directory.Exists(paths.PreviousInstallRoot) && !Directory.Exists(paths.RetiredPrevious))
            {
                Directory.Move(paths.PreviousInstallRoot, paths.RetiredPrevious);
            }

            DeleteDirectoryStrict(paths.RetiredPrevious);
        }

        DeleteDirectoryStrict(paths.Backup);
        DeleteDirectoryStrict(paths.Failed);
        DeleteFileStrict(JournalPath);
    }

    private static bool RuntimeStatesMatch(ManagedRuntimeState<TBackend> actual, ManagedRuntimeState<TBackend> expected)
    {
        return actual.Validity == ManagedRuntimeValidity.Active
               && EqualityComparer<TBackend>.Default.Equals(actual.DesiredBackend, expected.DesiredBackend)
               && string.Equals(actual.SourceRepository, expected.SourceRepository, StringComparison.Ordinal)
               && string.Equals(actual.SourceCommit, expected.SourceCommit, StringComparison.Ordinal)
               && actual.SourceSelection == expected.SourceSelection
               && actual.SourceRevisionMode == expected.SourceRevisionMode
               && string.Equals(actual.SourceRequestedCommit, expected.SourceRequestedCommit, StringComparison.Ordinal)
               && string.Equals(actual.SourceBuildPath, expected.SourceBuildPath, StringComparison.Ordinal)
               && string.Equals(actual.ServerSha256, expected.ServerSha256, StringComparison.Ordinal);
    }

    private async Task<bool> ManagedRuntimeBytesMatchStateAsync(ManagedRuntimeState<TBackend> state,
        string installRoot,
        CancellationToken ct)
    {
        if (state.SourceBuildPath is not { Length: > 0 } buildPath
            || state.ServerSha256 is not { Length: 64 } expectedSha
            || !expectedSha.All(Uri.IsHexDigit))
        {
            return false;
        }

        try
        {
            var fullInstallRoot = Path.GetFullPath(installRoot);
            var fullBuildPath = Path.GetFullPath(buildPath);
            var installPrefix = fullInstallRoot + Path.DirectorySeparatorChar;
            if (!string.Equals(fullBuildPath, fullInstallRoot, StringComparison.Ordinal)
                && !fullBuildPath.StartsWith(installPrefix,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return false;
            }

            var serverPath = Path.Combine(fullBuildPath, OperatingSystem.IsWindows() ? _serverName + ".exe" : _serverName);
            if (!File.Exists(serverPath) || new FileInfo(serverPath).LinkTarget is not null)
            {
                return false;
            }

            var actualSha = await FileSha256.ComputeAsync(serverPath, ct).ConfigureAwait(false);
            return string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException
                                              or NotSupportedException)
        {
            return false;
        }
    }

    private async Task RestorePreviousStateAsync(ManagedRuntimeState<TBackend>? previousState)
    {
        if (previousState is null)
        {
            await _runtimeStore.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
            _managedSignal.Clear();
            return;
        }

        await _runtimeStore.WriteAsync(previousState, CancellationToken.None).ConfigureAwait(false);
        if (previousState.Validity == ManagedRuntimeValidity.Active)
        {
            _managedSignal.SetActive(previousState.DesiredBackend);
        }
        else
        {
            _managedSignal.Clear();
        }
    }

    private void ValidateAdoptedServer(string installRoot, string serverPath)
    {
        var fullCacheRoot = Path.GetFullPath(_cacheRoot);
        var fullInstallRoot = Path.GetFullPath(installRoot);
        var fullServerPath = Path.GetFullPath(serverPath);
        var installPrefix = fullInstallRoot + Path.DirectorySeparatorChar;
        var cachePrefix = fullCacheRoot + Path.DirectorySeparatorChar;
        if (!fullInstallRoot.StartsWith(cachePrefix, StringComparison.Ordinal)
            || !fullServerPath.StartsWith(installPrefix, StringComparison.Ordinal)
            || !File.Exists(fullServerPath)
            || new FileInfo(fullServerPath).LinkTarget is not null)
        {
            throw RuntimeError($"The built {_serverName} failed managed-path validation.");
        }

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var serverMode = File.GetUnixFileMode(fullServerPath);
        if ((serverMode & UnixFileMode.OtherWrite) != UnixFileMode.None
            || (serverMode & UnixFileMode.UserExecute) == UnixFileMode.None)
        {
            throw RuntimeError($"The built {_serverName} has insecure permissions.");
        }

        // A world-writable ancestor would let another user swap the binary between validation and spawn, so the whole
        // chain up to the cache root is checked, not just the file.
        var directory = Path.GetDirectoryName(fullServerPath);
        while (!string.IsNullOrEmpty(directory) && directory.Length >= fullCacheRoot.Length)
        {
            if (new DirectoryInfo(directory).LinkTarget is not null
                || (File.GetUnixFileMode(directory) & UnixFileMode.OtherWrite) != UnixFileMode.None)
            {
                throw RuntimeError($"The built {_serverName} path chain is insecure.");
            }

            if (string.Equals(directory, fullCacheRoot, StringComparison.Ordinal))
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }
    }

    private string GetManagedInstallRoot(ManagedRuntimeState<TBackend> installed)
    {
        if (installed.SourceCommit.Length != 40 || !installed.SourceCommit.All(Uri.IsHexDigit))
        {
            throw RuntimeError($"The recorded managed {_runtimeLabel} commit is invalid.");
        }

        var root = Path.GetFullPath(RuntimeRoot);
        var installRoot = Path.GetFullPath(Path.Combine(root, BackendSlug(installed.DesiredBackend), installed.SourceCommit));
        if (!installRoot.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw RuntimeError($"The recorded managed {_runtimeLabel} path is outside the managed cache.");
        }

        return installRoot;
    }

    private void DeleteDirectoryStrict(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        if (Directory.Exists(path))
        {
            throw new IOException($"A managed {_runtimeLabel} directory could not be removed.");
        }
    }

    private void DeleteFileStrict(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        if (File.Exists(path))
        {
            throw new IOException($"The managed {_runtimeLabel} adoption journal could not be removed.");
        }
    }

    private sealed record AdoptionPaths
    {
        public required string Destination { get; init; }

        public required string Backup { get; init; }

        public required string Failed { get; init; }

        public required string? PreviousInstallRoot { get; init; }

        public required string? RetiredPrevious { get; init; }
    }
}
