namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>Owns the journaled directory-and-state transaction used to adopt a managed image runtime.</summary>
internal sealed class StableDiffusionCppRuntimeAdoption(
    string cacheRoot,
    IStableDiffusionInstalledRuntimeStore runtimeStore,
    IStableDiffusionManagedSourceBuildSignal managedSignal,
    ILogger logger)
{
    private string BuildRoot => Path.Combine(cacheRoot, "stable-diffusion.cpp", "source-build");
    private string JournalPath => Path.Combine(BuildRoot, "adoption-journal.json");
    private string RuntimeRoot => Path.Combine(cacheRoot, "stable-diffusion.cpp", "managed");

    public async Task RecoverAsync(CancellationToken ct)
    {
        if (!File.Exists(JournalPath))
        {
            return;
        }

        StableDiffusionCppAdoptionJournal journal;
        try
        {
            await using var stream = File.OpenRead(JournalPath);
            journal = await JsonSerializer.DeserializeAsync<StableDiffusionCppAdoptionJournal>(stream, cancellationToken: ct).ConfigureAwait(false)
                      ?? throw new StableDiffusionRuntimeException("The managed image runtime adoption journal is invalid.");
        }
        catch (JsonException exception)
        {
            throw new StableDiffusionRuntimeException("The managed image runtime adoption journal is invalid.", exception);
        }

        var paths = GetPaths(journal);
        var installed = await runtimeStore.ReadAsync(ct).ConfigureAwait(false);
        if (installed is not null && RuntimeStatesMatch(installed, journal.NewState) && Directory.Exists(paths.Destination))
        {
            try
            {
                CleanupCommitted(paths);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "A committed stable-diffusion.cpp runtime cleanup remains pending and will be retried.");
            }

            return;
        }

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

            throw new StableDiffusionRuntimeException("The previous managed image runtime backup is missing and the installed bytes cannot be safely identified.");
        }

        await RollbackAsync(journal, paths).ConfigureAwait(false);
    }

    public async Task AdoptAsync(string buildDir,
        string serverPath,
        StableDiffusionCppSourceBuildDescriptor descriptor,
        CancellationToken ct)
    {
        var relativeServer = Path.GetRelativePath(buildDir, serverPath);
        if (relativeServer.StartsWith("..", StringComparison.Ordinal))
        {
            throw new StableDiffusionRuntimeException("The built sd-server path escaped the build directory.");
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
            HardenManagedTree(staging);
            var stagedServer = Path.GetFullPath(Path.Combine(staging, relativeServer));
            ValidateAdoptedServer(staging, stagedServer);
            var digest = await ComputeSha256Async(stagedServer, ct).ConfigureAwait(false);
            var finalServer = Path.GetFullPath(Path.Combine(destination, relativeServer));
            var state = new StableDiffusionInstalledRuntimeState(StableDiffusionInstalledRuntimeValidity.Active,
                descriptor.Backend,
                descriptor.Repository,
                descriptor.ResolvedCommit!,
                descriptor.Source,
                descriptor.RevisionMode,
                descriptor.RequestedCommit,
                Path.GetDirectoryName(finalServer),
                digest,
                DateTimeOffset.UtcNow);
            var previousState = await runtimeStore.ReadAsync(ct).ConfigureAwait(false);
            var journal = new StableDiffusionCppAdoptionJournal(descriptor.BuildId,
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
                    throw new StableDiffusionRuntimeException("The managed image runtime adoption failed and its previous state could not be restored.",
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
                logger.LogWarning(exception, "The previous stable-diffusion.cpp runtime cleanup is pending and will be retried.");
            }
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    private async Task RollbackAsync(StableDiffusionCppAdoptionJournal journal, AdoptionPaths paths)
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
                throw new StableDiffusionRuntimeException("The previous managed image runtime backup is missing.");
            }

            Directory.Move(paths.Backup, paths.Destination);
        }

        if (paths.RetiredPrevious is not null && Directory.Exists(paths.RetiredPrevious))
        {
            if (paths.PreviousInstallRoot is null || Directory.Exists(paths.PreviousInstallRoot))
            {
                throw new StableDiffusionRuntimeException("The previous managed image runtime could not be recovered.");
            }

            Directory.Move(paths.RetiredPrevious, paths.PreviousInstallRoot);
        }

        await RestorePreviousStateAsync(journal.PreviousState).ConfigureAwait(false);
        DeleteDirectoryStrict(paths.Failed);
        DeleteFileStrict(JournalPath);
    }

    private async Task WriteJournalAsync(StableDiffusionCppAdoptionJournal journal, CancellationToken ct)
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

    private AdoptionPaths GetPaths(StableDiffusionCppAdoptionJournal journal)
    {
        if (journal.NewCommit.Length != 40
            || !journal.NewCommit.All(Uri.IsHexDigit)
            || journal.NewState.SourceCommit != journal.NewCommit
            || journal.NewState.DesiredBackend != journal.NewBackend)
        {
            throw new StableDiffusionRuntimeException("The managed image runtime adoption journal is invalid.");
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

    private static bool RuntimeStatesMatch(StableDiffusionInstalledRuntimeState actual, StableDiffusionInstalledRuntimeState expected)
    {
        return actual.Validity == StableDiffusionInstalledRuntimeValidity.Active
               && actual.DesiredBackend == expected.DesiredBackend
               && string.Equals(actual.SourceRepository, expected.SourceRepository, StringComparison.Ordinal)
               && string.Equals(actual.SourceCommit, expected.SourceCommit, StringComparison.Ordinal)
               && actual.SourceSelection == expected.SourceSelection
               && actual.SourceRevisionMode == expected.SourceRevisionMode
               && string.Equals(actual.SourceRequestedCommit, expected.SourceRequestedCommit, StringComparison.Ordinal)
               && string.Equals(actual.SourceBuildPath, expected.SourceBuildPath, StringComparison.Ordinal)
               && string.Equals(actual.ServerSha256, expected.ServerSha256, StringComparison.Ordinal);
    }

    private static async Task<bool> ManagedRuntimeBytesMatchStateAsync(StableDiffusionInstalledRuntimeState state,
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

            var serverPath = Path.Combine(fullBuildPath, OperatingSystem.IsWindows() ? "sd-server.exe" : "sd-server");
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

    private async Task RestorePreviousStateAsync(StableDiffusionInstalledRuntimeState? previousState)
    {
        if (previousState is null)
        {
            await runtimeStore.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
            managedSignal.Clear();
            return;
        }

        await runtimeStore.WriteAsync(previousState, CancellationToken.None).ConfigureAwait(false);
        if (previousState.Validity == StableDiffusionInstalledRuntimeValidity.Active)
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
            throw new StableDiffusionRuntimeException("The built sd-server failed managed-path validation.");
        }

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var serverMode = File.GetUnixFileMode(fullServerPath);
        if ((serverMode & UnixFileMode.OtherWrite) != UnixFileMode.None
            || (serverMode & UnixFileMode.UserExecute) == UnixFileMode.None)
        {
            throw new StableDiffusionRuntimeException("The built sd-server has insecure permissions.");
        }

        var directory = Path.GetDirectoryName(fullServerPath);
        while (!string.IsNullOrEmpty(directory) && directory.Length >= fullCacheRoot.Length)
        {
            if (new DirectoryInfo(directory).LinkTarget is not null
                || (File.GetUnixFileMode(directory) & UnixFileMode.OtherWrite) != UnixFileMode.None)
            {
                throw new StableDiffusionRuntimeException("The built sd-server path chain is insecure.");
            }

            if (string.Equals(directory, fullCacheRoot, StringComparison.Ordinal))
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }
    }

    private string GetManagedInstallRoot(StableDiffusionInstalledRuntimeState installed)
    {
        if (installed.SourceCommit.Length != 40 || !installed.SourceCommit.All(Uri.IsHexDigit))
        {
            throw new StableDiffusionRuntimeException("The recorded managed runtime commit is invalid.");
        }

        var root = Path.GetFullPath(RuntimeRoot);
        var installRoot = Path.GetFullPath(Path.Combine(root, BackendSlug(installed.DesiredBackend), installed.SourceCommit));
        if (!installRoot.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new StableDiffusionRuntimeException("The recorded managed runtime path is outside the managed cache.");
        }

        return installRoot;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static void HardenManagedTree(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root))
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var existing = File.GetUnixFileMode(file);
            var execute = existing & UnixFileMode.UserExecute;
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | execute);
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

    private static string BackendSlug(SdGpuBackend backend)
    {
        return backend switch
        {
            SdGpuBackend.Cuda => "cuda",
            SdGpuBackend.Vulkan => "vulkan",
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
            throw new IOException("A managed image runtime directory could not be removed.");
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
            throw new IOException("The managed image runtime adoption journal could not be removed.");
        }
    }

    private sealed record AdoptionPaths(
        string Destination,
        string Backup,
        string Failed,
        string? PreviousInstallRoot,
        string? RetiredPrevious);
}

internal sealed record StableDiffusionCppAdoptionJournal(
    Guid BuildId,
    SdGpuBackend NewBackend,
    string NewCommit,
    bool HadPreviousDestination,
    StableDiffusionInstalledRuntimeState? PreviousState,
    StableDiffusionInstalledRuntimeState NewState);
