namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Atomic owner-only state store for the managed whisper.cpp runtime record, with a redundant desired-selection
///     record so a corrupt primary still fails CLOSED.
/// </summary>
/// <remarks>
///     It degrades to a tombstone naming the operator's selection rather than to "no managed runtime", which would silently hand back a
///     prebuilt that contradicts the UI. Writes land under <c>{cacheRoot}/whisper.cpp/</c>, and the file names deliberately match the
///     llama.cpp and stable-diffusion.cpp stores: collision is impossible by the PARENT segment (llama writes at the bare cache root,
///     stable-diffusion.cpp under <c>stable-diffusion.cpp/</c>), and identical names are what make the three stores recognisable as the
///     same thing.
/// </remarks>
public sealed class WhisperInstalledRuntimeStore : IWhisperInstalledRuntimeStore, IDisposable
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

    /// <summary>Creates the store under <paramref name="cacheRoot" />, defaulting to the app's local-data directory.</summary>
    public WhisperInstalledRuntimeStore(string? cacheRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(cacheRoot) ? RuntimeCacheDirectory.Resolve() : cacheRoot;
        var stateRoot = Path.Combine(root, "whisper.cpp");
        _statePath = Path.Combine(stateRoot, StateFileName);
        _desiredPath = Path.Combine(stateRoot, DesiredFileName);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lock.Dispose();
    }

    /// <inheritdoc />
    public async Task<WhisperInstalledRuntimeState?> ReadAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await TryReadAsync<WhisperInstalledRuntimeState>(_statePath, ct).ConfigureAwait(false);
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

            // The primary record is missing or unusable. The desired record still names what the operator selected, so
            // the answer is a tombstone carrying that selection, never "nothing is installed".
            var desired = await TryReadAsync<DesiredRuntimeState>(_desiredPath, ct).ConfigureAwait(false);
            return desired is null || !IsValidDesired(desired)
                ? null
                : new WhisperInstalledRuntimeState(WhisperInstalledRuntimeValidity.Invalid,
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

    /// <inheritdoc />
    public async Task WriteAsync(WhisperInstalledRuntimeState state, CancellationToken ct)
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

            // Desired first: a crash between the two writes must leave the selection recoverable, not the other way round.
            await WriteAtomicAsync(_desiredPath, desired, ct).ConfigureAwait(false);
            await WriteAtomicAsync(_statePath, state, ct).ConfigureAwait(false);
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

    private static bool IsValidState(WhisperInstalledRuntimeState state)
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

        if (state.Validity == WhisperInstalledRuntimeValidity.Invalid)
        {
            return !string.IsNullOrWhiteSpace(state.InvalidReason);
        }

        return state.Validity == WhisperInstalledRuntimeValidity.Active
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

    private static bool IsValidDesiredCore(WhisperBackend backend,
        string? repository,
        string? commit,
        WhisperCppSourceSelection sourceSelection,
        WhisperCppSourceRevisionMode revisionMode,
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

        // The selection and the revision mode have to agree, or a record could claim the official source while naming
        // a commit nobody pinned.
        return (sourceSelection, revisionMode) switch
        {
            (WhisperCppSourceSelection.Official, WhisperCppSourceRevisionMode.EnginePinned) =>
                string.Equals(repository, WhisperCppSourceBuildRequestValidation.OfficialRepository, StringComparison.Ordinal)
                && string.Equals(commit, WhisperCppReleasePins.PinnedSourceCommitSha, StringComparison.OrdinalIgnoreCase)
                && requestedCommit is null,
            (WhisperCppSourceSelection.Custom, WhisperCppSourceRevisionMode.DefaultBranch) =>
                requestedCommit is null,
            (WhisperCppSourceSelection.Custom, WhisperCppSourceRevisionMode.ExplicitCommit) =>
                IsHex(requestedCommit, expectedLength: 40)
                && string.Equals(commit, requestedCommit, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static WhisperInstalledRuntimeState? TryCreateTombstone(WhisperInstalledRuntimeState state, string reason)
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
                Validity = WhisperInstalledRuntimeValidity.Invalid,
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
            return string.Equals(WhisperCppSourceBuildRequestValidation.NormalizeGitHubRepository(value),
                value,
                StringComparison.Ordinal);
        }
        catch (WhisperRuntimeException)
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
            // Best-effort temp cleanup; never mask the real failure.
        }
    }

    /// <summary>The redundant selection record, written beside the primary so a corrupt primary still fails closed.</summary>
    private sealed record DesiredRuntimeState(
        WhisperBackend Backend,
        string Repository,
        string Commit,
        WhisperCppSourceSelection SourceSelection,
        WhisperCppSourceRevisionMode RevisionMode,
        string? RequestedCommit,
        DateTimeOffset InstalledAtUtc);
}
