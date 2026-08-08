namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>Atomic owner-only state store with a redundant desired-selection record for fail-closed recovery.</summary>
public sealed class StableDiffusionInstalledRuntimeStore : IStableDiffusionInstalledRuntimeStore, IDisposable
{
    private const string DesiredFileName = "desired-runtime.json";
    private const string StateFileName = "installed-runtime.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _desiredPath;
    private readonly SemaphoreSlim _lock = new(initialCount: 1, maxCount: 1);
    private readonly string _statePath;

    public StableDiffusionInstalledRuntimeStore(string? cacheRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(cacheRoot) ? DefaultCacheRoot() : cacheRoot;
        var stateRoot = Path.Combine(root, "stable-diffusion.cpp");
        _statePath = Path.Combine(stateRoot, StateFileName);
        _desiredPath = Path.Combine(stateRoot, DesiredFileName);
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    public async Task<StableDiffusionInstalledRuntimeState?> ReadAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await TryReadAsync<StableDiffusionInstalledRuntimeState>(_statePath, ct).ConfigureAwait(false);
            if (state is not null)
            {
                if (IsValidState(state))
                {
                    return state;
                }

                var stateTombstone = TryCreateTombstone(state, "The managed runtime record is semantically invalid.");
                if (stateTombstone is not null)
                {
                    return stateTombstone;
                }
            }

            var desired = await TryReadAsync<DesiredRuntimeState>(_desiredPath, ct).ConfigureAwait(false);
            return desired is null || !IsValidDesired(desired)
                ? null
                : new StableDiffusionInstalledRuntimeState(StableDiffusionInstalledRuntimeValidity.Invalid,
                    desired.Backend,
                    desired.Repository,
                    desired.Commit,
                    desired.SourceSelection,
                    desired.RevisionMode,
                    desired.RequestedCommit,
                    SourceBuildPath: null,
                    ServerSha256: null,
                    desired.InstalledAtUtc,
                    "The managed runtime record is missing or corrupt.");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task WriteAsync(StableDiffusionInstalledRuntimeState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!IsValidState(state))
        {
            throw new ArgumentException("The managed runtime state is semantically invalid.", nameof(state));
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOwnerOnlyDirectory(Path.GetDirectoryName(_statePath)!);
            var desired = new DesiredRuntimeState(state.DesiredBackend,
                state.SourceRepository,
                state.SourceCommit,
                state.SourceSelection,
                state.SourceRevisionMode,
                state.SourceRequestedCommit,
                state.InstalledAtUtc);
            await WriteAtomicAsync(_desiredPath, desired, ct).ConfigureAwait(false);
            await WriteAtomicAsync(_statePath, state, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            TryDelete(_statePath);
            TryDelete(_desiredPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<T?> TryReadAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private static bool IsValidState(StableDiffusionInstalledRuntimeState state)
    {
        if (!Enum.IsDefined(state.Validity)
            || !IsValidDesiredCore(state.DesiredBackend,
                state.SourceRepository,
                state.SourceCommit,
                state.SourceSelection,
                state.SourceRevisionMode,
                state.SourceRequestedCommit,
                state.InstalledAtUtc))
        {
            return false;
        }

        if (state.Validity == StableDiffusionInstalledRuntimeValidity.Invalid)
        {
            return !string.IsNullOrWhiteSpace(state.InvalidReason);
        }

        return state.Validity == StableDiffusionInstalledRuntimeValidity.Active
               && !string.IsNullOrWhiteSpace(state.SourceBuildPath)
               && Path.IsPathFullyQualified(state.SourceBuildPath)
               && IsHex(state.ServerSha256, expectedLength: 64)
               && string.IsNullOrWhiteSpace(state.InvalidReason);
    }

    private static bool IsValidDesired(DesiredRuntimeState desired)
    {
        return IsValidDesiredCore(desired.Backend,
            desired.Repository,
            desired.Commit,
            desired.SourceSelection,
            desired.RevisionMode,
            desired.RequestedCommit,
            desired.InstalledAtUtc);
    }

    private static bool IsValidDesiredCore(SdGpuBackend backend,
        string? repository,
        string? commit,
        StableDiffusionCppSourceSelection sourceSelection,
        StableDiffusionCppSourceRevisionMode revisionMode,
        string? requestedCommit,
        DateTimeOffset installedAtUtc)
    {
        if (!Enum.IsDefined(backend)
            || !Enum.IsDefined(sourceSelection)
            || !Enum.IsDefined(revisionMode)
            || !IsCanonicalGitHubRepository(repository)
            || !IsHex(commit, expectedLength: 40)
            || installedAtUtc == default)
        {
            return false;
        }

        return (sourceSelection, revisionMode) switch
        {
            (StableDiffusionCppSourceSelection.Official, StableDiffusionCppSourceRevisionMode.EnginePinned) =>
                string.Equals(repository, StableDiffusionCppSourceBuildRequestValidation.OfficialRepository, StringComparison.Ordinal)
                && string.Equals(commit, StableDiffusionReleasePins.PinnedSourceCommitSha, StringComparison.OrdinalIgnoreCase)
                && requestedCommit is null,
            (StableDiffusionCppSourceSelection.Custom, StableDiffusionCppSourceRevisionMode.DefaultBranch) =>
                requestedCommit is null,
            (StableDiffusionCppSourceSelection.Custom, StableDiffusionCppSourceRevisionMode.ExplicitCommit) =>
                IsHex(requestedCommit, expectedLength: 40)
                && string.Equals(commit, requestedCommit, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static StableDiffusionInstalledRuntimeState? TryCreateTombstone(StableDiffusionInstalledRuntimeState state,
        string reason)
    {
        return IsValidDesiredCore(state.DesiredBackend,
            state.SourceRepository,
            state.SourceCommit,
            state.SourceSelection,
            state.SourceRevisionMode,
            state.SourceRequestedCommit,
            state.InstalledAtUtc)
            ? state with
            {
                Validity = StableDiffusionInstalledRuntimeValidity.Invalid,
                SourceBuildPath = null,
                ServerSha256 = null,
                InvalidReason = reason
            }
            : null;
    }

    private static bool IsCanonicalGitHubRepository(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            return string.Equals(StableDiffusionCppSourceBuildRequestValidation.NormalizeGitHubRepository(value),
                value,
                StringComparison.Ordinal);
        }
        catch (StableDiffusionRuntimeException)
        {
            return false;
        }
    }

    private static bool IsHex(string? value, int expectedLength)
    {
        return value is { Length: > 0 }
               && value.Length == expectedLength
               && value.All(Uri.IsHexDigit);
    }

    private static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken ct)
    {
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = CreateOwnerOnly(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, ct).ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static FileStream CreateOwnerOnly(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    private static void EnsureOwnerOnlyDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    private static string DefaultCacheRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XE-Local-AI-Engine");
    }

    private sealed record DesiredRuntimeState(
        SdGpuBackend Backend,
        string Repository,
        string Commit,
        StableDiffusionCppSourceSelection SourceSelection,
        StableDiffusionCppSourceRevisionMode RevisionMode,
        string? RequestedCommit,
        DateTimeOffset InstalledAtUtc);
}
