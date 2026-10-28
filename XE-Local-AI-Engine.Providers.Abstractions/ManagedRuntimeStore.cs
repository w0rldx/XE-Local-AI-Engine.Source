namespace XE_Local_AI_Engine.Providers.Abstractions;

using System.Text.Json;

/// <summary>
///     Atomic owner-only state store for one runtime's managed source-built runtime record, with a redundant
///     desired-selection record so a corrupt primary still fails CLOSED.
/// </summary>
/// <remarks>
///     It degrades to a tombstone naming the operator's selection rather than to "no managed runtime", which would silently hand back a
///     prebuilt that contradicts the UI. Writes land under <c>{cacheRoot}/{directorySegment}/</c>, and the file names deliberately match
///     the llama.cpp store: collision is impossible by the PARENT segment (llama writes at the bare cache root), and identical names are
///     what make the stores recognisable as the same thing. A record whose backend is not a defined
///     <typeparamref name="TBackend" /> value is invalid, so each runtime accepts only the backends it can build.
/// </remarks>
public abstract class ManagedRuntimeStore<TBackend> : IManagedRuntimeStore<TBackend>, IDisposable
    where TBackend : struct, Enum
{
    private const string DesiredFileName = "desired-runtime.json";
    private const string StateFileName = "installed-runtime.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _desiredPath;
    private readonly SemaphoreSlim _lock = new(initialCount: 1, maxCount: 1);
    private readonly string _officialRepository;
    private readonly string _pinnedSourceCommitSha;
    private readonly string _statePath;

    /// <summary>
    ///     Creates the store under <paramref name="cacheRoot" /> (defaulting to the app's local-data directory) for the
    ///     runtime whose official source is <paramref name="officialRepository" /> at <paramref name="pinnedSourceCommitSha" />.
    /// </summary>
    protected ManagedRuntimeStore(string? cacheRoot,
        string directorySegment,
        string officialRepository,
        string pinnedSourceCommitSha)
    {
        var root = string.IsNullOrWhiteSpace(cacheRoot) ? RuntimeCacheDirectory.Resolve() : cacheRoot;
        var stateRoot = Path.Combine(root, directorySegment);
        _statePath = Path.Combine(stateRoot, StateFileName);
        _desiredPath = Path.Combine(stateRoot, DesiredFileName);
        _officialRepository = officialRepository;
        _pinnedSourceCommitSha = pinnedSourceCommitSha;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async Task<ManagedRuntimeState<TBackend>?> ReadAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await TryReadAsync<ManagedRuntimeState<TBackend>>(_statePath, ct).ConfigureAwait(false);
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
                : new ManagedRuntimeState<TBackend>(ManagedRuntimeValidity.Invalid,
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
    public async Task WriteAsync(ManagedRuntimeState<TBackend> state, CancellationToken ct)
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
            BestEffortFile.TryDeleteFile(_statePath);
            BestEffortFile.TryDeleteFile(_desiredPath);
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

    private bool IsValidState(ManagedRuntimeState<TBackend> state)
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

        if (state.Validity == ManagedRuntimeValidity.Invalid)
        {
            return !string.IsNullOrWhiteSpace(state.InvalidReason);
        }

        return state.Validity == ManagedRuntimeValidity.Active
               && !string.IsNullOrWhiteSpace(state.SourceBuildPath)
               && Path.IsPathFullyQualified(state.SourceBuildPath)
               && IsHex(state.ServerSha256, expectedLength: 64)
               && string.IsNullOrWhiteSpace(state.InvalidReason);
    }

    private bool IsValidDesired(DesiredRuntimeState desired)
    {
        return IsValidDesiredCore(desired.Backend,
            desired.Repository,
            desired.Commit,
            desired.SourceSelection,
            desired.RevisionMode,
            desired.RequestedCommit,
            desired.InstalledAtUtc);
    }

    private bool IsValidDesiredCore(TBackend backend,
        string? repository,
        string? commit,
        ManagedSourceSelection sourceSelection,
        ManagedSourceRevisionMode revisionMode,
        string? requestedCommit,
        DateTimeOffset installedAtUtc)
    {
        if (!Enum.IsDefined(backend)
            || !Enum.IsDefined(sourceSelection)
            || !Enum.IsDefined(revisionMode)
            || string.IsNullOrWhiteSpace(repository)
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
            (ManagedSourceSelection.Official, ManagedSourceRevisionMode.EnginePinned) =>
                string.Equals(repository, _officialRepository, StringComparison.Ordinal)
                && string.Equals(commit, _pinnedSourceCommitSha, StringComparison.OrdinalIgnoreCase)
                && requestedCommit is null,
            (ManagedSourceSelection.Custom, ManagedSourceRevisionMode.DefaultBranch) =>
                requestedCommit is null,
            (ManagedSourceSelection.Custom, ManagedSourceRevisionMode.ExplicitCommit) =>
                IsHex(requestedCommit, expectedLength: 40)
                && string.Equals(commit, requestedCommit, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private ManagedRuntimeState<TBackend>? TryCreateTombstone(ManagedRuntimeState<TBackend> state, string reason)
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
                Validity = ManagedRuntimeValidity.Invalid,
                SourceBuildPath = null,
                ServerSha256 = null,
                InvalidReason = reason
            }
            : null;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lock.Dispose();
        }
    }

    /// <summary>Whether <paramref name="value" /> is already the runtime's normalized form of a GitHub repository URL.</summary>
    protected abstract bool IsCanonicalGitHubRepository(string value);

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
            BestEffortFile.TryDeleteFile(tempPath);
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

    /// <summary>The redundant selection record, written beside the primary so a corrupt primary still fails closed.</summary>
    private sealed record DesiredRuntimeState(
        TBackend Backend,
        string Repository,
        string Commit,
        ManagedSourceSelection SourceSelection,
        ManagedSourceRevisionMode RevisionMode,
        string? RequestedCommit,
        DateTimeOffset InstalledAtUtc);
}
