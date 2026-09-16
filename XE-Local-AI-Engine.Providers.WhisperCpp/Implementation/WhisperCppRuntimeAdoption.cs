namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Owns the journaled directory-and-state transaction that turns a finished build tree into the node's managed
///     whisper.cpp runtime.
/// </summary>
/// <remarks>
///     <para>
///         The transaction is journal-first: the intent is written to disk before any directory moves, so a host that
///         dies mid-adoption can be reconciled on the next start rather than left with a record and a tree that
///         disagree. <see cref="RecoverAsync" /> is that reconciliation and is the reason the journal exists.
///     </para>
///     <para>
///         <b>Link policy.</b> A whisper.cpp build's output legitimately contains SONAME symlink chains
///         (<c>libwhisper.so</c> → <c>libwhisper.so.1</c> → <c>libwhisper.so.1.9.4</c>), so links cannot simply be
///         rejected. They also cannot simply be accepted: hardening walks the tree setting permissions, and a link
///         that escapes the staging root would have it chmod a file outside. Every link is therefore checked before
///         anything is modified — relative and resolving inside the tree is accepted, anything else fails the
///         adoption — and hardening then sets modes on real entries only, never through a link.
///     </para>
/// </remarks>
internal sealed class WhisperCppRuntimeAdoption(
    string cacheRoot,
    IWhisperInstalledRuntimeStore runtimeStore,
    IWhisperManagedSourceBuildSignal managedSignal,
    TimeProvider timeProvider,
    ILogger logger)
{
    private string BuildRoot => Path.Combine(cacheRoot, "whisper.cpp", "source-build");
    private string JournalPath => Path.Combine(BuildRoot, "adoption-journal.json");
    private string RuntimeRoot => Path.Combine(cacheRoot, "whisper.cpp", "managed");

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

        WhisperCppAdoptionJournal journal;
        try
        {
            await using var stream = File.OpenRead(JournalPath);
            journal = await JsonSerializer.DeserializeAsync<WhisperCppAdoptionJournal>(stream, cancellationToken: ct).ConfigureAwait(false)
                      ?? throw new WhisperRuntimeException("The managed whisper.cpp runtime adoption journal is invalid.");
        }
        catch (JsonException exception)
        {
            throw new WhisperRuntimeException("The managed whisper.cpp runtime adoption journal is invalid.", exception);
        }

        var paths = GetPaths(journal);
        var installed = await runtimeStore.ReadAsync(ct).ConfigureAwait(false);

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
                logger.LogWarning(exception, "A committed whisper.cpp runtime cleanup remains pending and will be retried.");
            }

            return;
        }

        // The swap had not started: the destination still holds the PREVIOUS runtime and there is no backup to
        // restore because none was ever taken. Proven on bytes, not on the journal's word, before the journal is
        // dropped — otherwise a half-written destination would be adopted as healthy.
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

            throw new WhisperRuntimeException("The previous managed whisper.cpp runtime backup is missing and the installed bytes cannot be safely identified.");
        }

        await RollbackAsync(journal, paths).ConfigureAwait(false);
    }

    /// <summary>
    ///     Moves a verified build tree into the managed install root and publishes the record that makes it the node's
    ///     runtime, rolling back to the previous runtime if any step fails.
    /// </summary>
    public async Task AdoptAsync(string buildDir,
        string serverPath,
        WhisperCppSourceBuildDescriptor descriptor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var relativeServer = Path.GetRelativePath(buildDir, serverPath);
        if (relativeServer.StartsWith("..", StringComparison.Ordinal))
        {
            throw new WhisperRuntimeException("The built whisper-server path escaped the build directory.");
        }

        var backendRoot = Path.Combine(RuntimeRoot, BackendSlug(descriptor.Backend));
        var destination = Path.Combine(backendRoot, descriptor.ResolvedCommit!);
        var staging = Path.Combine(backendRoot, $".staging-{descriptor.ResolvedCommit}-{descriptor.BuildId:N}");
        var backup = Path.Combine(backendRoot, $".backup-{descriptor.ResolvedCommit}-{descriptor.BuildId:N}");
        var failed = Path.Combine(backendRoot, $".failed-{descriptor.ResolvedCommit}-{descriptor.BuildId:N}");
        CreateOwnerOnlyDirectory(RuntimeRoot);
        CreateOwnerOnlyDirectory(backendRoot);
        TryDeleteDirectory(staging);
        TryDeleteDirectory(backup);
        TryDeleteDirectory(failed);
        Directory.Move(buildDir, staging);
        try
        {
            // Order is load-bearing: link validation, THEN hardening, THEN the swap. Hardening first would chmod
            // through an escaping link before anything had judged it.
            PrepareStagingTree(staging);
            var stagedServer = Path.GetFullPath(Path.Combine(staging, relativeServer));
            ValidateAdoptedServer(staging, stagedServer);
            var digest = await ComputeSha256Async(stagedServer, ct).ConfigureAwait(false);
            var finalServer = Path.GetFullPath(Path.Combine(destination, relativeServer));
            var state = new WhisperInstalledRuntimeState(WhisperInstalledRuntimeValidity.Active,
                descriptor.Backend,
                descriptor.Repository,
                descriptor.ResolvedCommit!,
                descriptor.Source,
                descriptor.RevisionMode,
                descriptor.RequestedCommit,
                Path.GetDirectoryName(finalServer),
                digest,
                timeProvider.GetUtcNow());
            var previousState = await runtimeStore.ReadAsync(ct).ConfigureAwait(false);
            var journal = new WhisperCppAdoptionJournal(descriptor.BuildId,
                descriptor.Backend,
                descriptor.ResolvedCommit!,
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
                await runtimeStore.WriteAsync(state, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception adoptionException)
            {
                try
                {
                    await RollbackAsync(journal, paths).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    managedSignal.Clear();
                    throw new WhisperRuntimeException("The managed whisper.cpp runtime adoption failed and its previous state could not be restored.",
                        new AggregateException(adoptionException, rollbackException));
                }

                ExceptionDispatchInfo.Capture(adoptionException).Throw();
                throw;
            }

            managedSignal.SetActive(descriptor.Backend);
            try
            {
                CleanupCommitted(paths);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "The previous whisper.cpp runtime cleanup is pending and will be retried.");
            }
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    internal static void CreateOwnerOnlyDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    ///     Validates every link in the staging tree and then hardens the real entries it found. One walk does both,
    ///     which is what guarantees hardening can only ever touch entries the validation already judged.
    /// </summary>
    private static void PrepareStagingTree(string staging)
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

    // Deliberately hand-rolled rather than SearchOption.AllDirectories: recursion must stop at a link instead of
    // descending through one, or a directory link pointing outside the tree would be walked — and hardened — before
    // anything judged it.
    private static void WalkValidatingLinks(string root, string directory, List<string> directories, List<string> files)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            // FileSystemInfo.LinkTarget reports the entry's own link, whatever the target's kind, so this recognises a
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
    private static void ValidateLink(string root, string linkPath, string linkTarget)
    {
        if (Path.IsPathRooted(linkTarget))
        {
            throw new WhisperRuntimeException("The source build produced a runtime containing an unsafe link.");
        }

        var containingDirectory = Path.GetDirectoryName(Path.GetFullPath(linkPath))
                                  ?? throw new WhisperRuntimeException("The source build produced a runtime containing an unsafe link.");
        var resolved = Path.GetFullPath(Path.Combine(containingDirectory, linkTarget));
        var rootPrefix = root + Path.DirectorySeparatorChar;
        if (!string.Equals(resolved, root, StringComparison.Ordinal) && !resolved.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new WhisperRuntimeException("The source build produced a runtime containing an unsafe link.");
        }
    }

    private async Task RollbackAsync(WhisperCppAdoptionJournal journal, AdoptionPaths paths)
    {
        managedSignal.Clear();
        if (Directory.Exists(paths.Destination))
        {
            DeleteDirectoryStrict(paths.Failed);
            Directory.Move(paths.Destination, paths.Failed);
        }

        if (journal.HadPreviousDestination)
        {
            if (!Directory.Exists(paths.Backup))
            {
                throw new WhisperRuntimeException("The previous managed whisper.cpp runtime backup is missing.");
            }

            Directory.Move(paths.Backup, paths.Destination);
        }

        if (paths.RetiredPrevious is not null && Directory.Exists(paths.RetiredPrevious))
        {
            if (paths.PreviousInstallRoot is null || Directory.Exists(paths.PreviousInstallRoot))
            {
                throw new WhisperRuntimeException("The previous managed whisper.cpp runtime could not be recovered.");
            }

            Directory.Move(paths.RetiredPrevious, paths.PreviousInstallRoot);
        }

        await RestorePreviousStateAsync(journal.PreviousState).ConfigureAwait(false);
        DeleteDirectoryStrict(paths.Failed);
        DeleteFileStrict(JournalPath);
    }

    private async Task WriteJournalAsync(WhisperCppAdoptionJournal journal, CancellationToken ct)
    {
        CreateOwnerOnlyDirectory(BuildRoot);
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

    private AdoptionPaths GetPaths(WhisperCppAdoptionJournal journal)
    {
        if (journal.NewCommit.Length != 40
            || !journal.NewCommit.All(Uri.IsHexDigit)
            || journal.NewState.SourceCommit != journal.NewCommit
            || journal.NewState.DesiredBackend != journal.NewBackend)
        {
            throw new WhisperRuntimeException("The managed whisper.cpp runtime adoption journal is invalid.");
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
            if (!PathsEqual(previousInstallRoot, destination))
            {
                retiredPrevious = Path.Combine(Path.GetDirectoryName(previousInstallRoot)!,
                    $".retired-{journal.PreviousState.SourceCommit}-{journal.BuildId:N}");
            }
        }

        return new AdoptionPaths(destination, backup, failed, previousInstallRoot, retiredPrevious);
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

    private static bool RuntimeStatesMatch(WhisperInstalledRuntimeState actual, WhisperInstalledRuntimeState expected)
    {
        return actual.Validity == WhisperInstalledRuntimeValidity.Active
               && actual.DesiredBackend == expected.DesiredBackend
               && string.Equals(actual.SourceRepository, expected.SourceRepository, StringComparison.Ordinal)
               && string.Equals(actual.SourceCommit, expected.SourceCommit, StringComparison.Ordinal)
               && actual.SourceSelection == expected.SourceSelection
               && actual.SourceRevisionMode == expected.SourceRevisionMode
               && string.Equals(actual.SourceRequestedCommit, expected.SourceRequestedCommit, StringComparison.Ordinal)
               && string.Equals(actual.SourceBuildPath, expected.SourceBuildPath, StringComparison.Ordinal)
               && string.Equals(actual.ServerSha256, expected.ServerSha256, StringComparison.Ordinal);
    }

    private static async Task<bool> ManagedRuntimeBytesMatchStateAsync(WhisperInstalledRuntimeState state,
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

            var serverPath = Path.Combine(fullBuildPath, OperatingSystem.IsWindows() ? "whisper-server.exe" : "whisper-server");
            if (!File.Exists(serverPath) || new FileInfo(serverPath).LinkTarget is not null)
            {
                return false;
            }

            var actualSha = await ComputeSha256Async(serverPath, ct).ConfigureAwait(false);
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

    private async Task RestorePreviousStateAsync(WhisperInstalledRuntimeState? previousState)
    {
        if (previousState is null)
        {
            await runtimeStore.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
            managedSignal.Clear();
            return;
        }

        await runtimeStore.WriteAsync(previousState, CancellationToken.None).ConfigureAwait(false);
        if (previousState.Validity == WhisperInstalledRuntimeValidity.Active)
        {
            managedSignal.SetActive(previousState.DesiredBackend);
        }
        else
        {
            managedSignal.Clear();
        }
    }

    private void ValidateAdoptedServer(string installRoot, string serverPath)
    {
        var fullCacheRoot = Path.GetFullPath(cacheRoot);
        var fullInstallRoot = Path.GetFullPath(installRoot);
        var fullServerPath = Path.GetFullPath(serverPath);
        var installPrefix = fullInstallRoot + Path.DirectorySeparatorChar;
        var cachePrefix = fullCacheRoot + Path.DirectorySeparatorChar;
        if (!fullInstallRoot.StartsWith(cachePrefix, StringComparison.Ordinal)
            || !fullServerPath.StartsWith(installPrefix, StringComparison.Ordinal)
            || !File.Exists(fullServerPath)
            || new FileInfo(fullServerPath).LinkTarget is not null)
        {
            throw new WhisperRuntimeException("The built whisper-server failed managed-path validation.");
        }

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var serverMode = File.GetUnixFileMode(fullServerPath);
        if ((serverMode & UnixFileMode.OtherWrite) != UnixFileMode.None
            || (serverMode & UnixFileMode.UserExecute) == UnixFileMode.None)
        {
            throw new WhisperRuntimeException("The built whisper-server has insecure permissions.");
        }

        // A world-writable ancestor would let another user swap the binary between validation and spawn, so the whole
        // chain up to the cache root is checked, not just the file.
        var directory = Path.GetDirectoryName(fullServerPath);
        while (!string.IsNullOrEmpty(directory) && directory.Length >= fullCacheRoot.Length)
        {
            if (new DirectoryInfo(directory).LinkTarget is not null
                || (File.GetUnixFileMode(directory) & UnixFileMode.OtherWrite) != UnixFileMode.None)
            {
                throw new WhisperRuntimeException("The built whisper-server path chain is insecure.");
            }

            if (string.Equals(directory, fullCacheRoot, StringComparison.Ordinal))
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }
    }

    private string GetManagedInstallRoot(WhisperInstalledRuntimeState installed)
    {
        if (installed.SourceCommit.Length != 40 || !installed.SourceCommit.All(Uri.IsHexDigit))
        {
            throw new WhisperRuntimeException("The recorded managed whisper.cpp runtime commit is invalid.");
        }

        var root = Path.GetFullPath(RuntimeRoot);
        var installRoot = Path.GetFullPath(Path.Combine(root, BackendSlug(installed.DesiredBackend), installed.SourceCommit));
        if (!installRoot.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new WhisperRuntimeException("The recorded managed whisper.cpp runtime path is outside the managed cache.");
        }

        return installRoot;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static string BackendSlug(WhisperBackend backend)
    {
        return backend switch
        {
            WhisperBackend.Cuda => "cuda",
            _ => "cpu"
        };
    }

    private static bool PathsEqual(string first, string second)
    {
        return string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static void TryDeleteDirectory(string path)
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
            // Best-effort cleanup; the caller reports the primary operation.
        }
    }

    private static void DeleteDirectoryStrict(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        if (Directory.Exists(path))
        {
            throw new IOException("A managed whisper.cpp runtime directory could not be removed.");
        }
    }

    private static void DeleteFileStrict(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        if (File.Exists(path))
        {
            throw new IOException("The managed whisper.cpp runtime adoption journal could not be removed.");
        }
    }

    private sealed record AdoptionPaths(
        string Destination,
        string Backup,
        string Failed,
        string? PreviousInstallRoot,
        string? RetiredPrevious);
}

/// <summary>The intent record written before an adoption touches a directory, and the input to its recovery.</summary>
internal sealed record WhisperCppAdoptionJournal(
    Guid BuildId,
    WhisperBackend NewBackend,
    string NewCommit,
    bool HadPreviousDestination,
    WhisperInstalledRuntimeState? PreviousState,
    WhisperInstalledRuntimeState NewState);
