namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>One managed source build, as it was resolved from the operator's request.</summary>
/// <remarks>
///     <see cref="WhisperCppSourceSelection" />, <see cref="WhisperCppSourceRevisionMode" /> and
///     <c>WhisperCppSourceBuildRequest</c> deliberately live in <c>Contracts/WhisperCppSourceSelection.cs</c> instead
///     of here: <c>WhisperInstalledRuntimeState</c> and its store's validation reference them, and both exist before
///     this lane does. That is the one place the whisper layout departs from the image-runtime precedent, which keeps
///     its enums inside the service interface file.
/// </remarks>
public sealed record WhisperCppSourceBuildDescriptor
{
    /// <summary>The acceleration backend being compiled for.</summary>
    public required WhisperBackend Backend { get; init; }

    /// <summary>Official or operator-supplied repository.</summary>
    public required WhisperCppSourceSelection Source { get; init; }

    /// <summary>The canonical repository URL the build fetches from.</summary>
    public required string Repository { get; init; }

    /// <summary>How the revision was chosen.</summary>
    public required WhisperCppSourceRevisionMode RevisionMode { get; init; }

    /// <summary>The commit the operator asked for, when they asked for one.</summary>
    public required string? RequestedCommit { get; init; }

    /// <summary>The commit the checkout actually landed on; null until the verify phase.</summary>
    public required string? ResolvedCommit { get; init; }

    /// <summary>Identifies this build across its status polls and its adoption journal.</summary>
    public Guid BuildId { get; init; }
}

/// <summary>Where a managed source build has got to.</summary>
public enum WhisperCppSourceBuildPhase
{
    Idle = 0,
    Cloning = 1,
    Verifying = 2,
    Configuring = 3,
    Building = 4,
    SmokeTesting = 5,
    Adopting = 6,
    Removing = 7,
    Completed = 8,
    Cancelled = 9,
    Failed = 10
}

/// <summary>Wire projection for <see cref="WhisperCppSourceBuildPhase" />.</summary>
public static class WhisperCppSourceBuildPhaseExtensions
{
    /// <summary>The camel-case token the local API and the SPA exchange for a phase.</summary>
    public static string ToWireString(this WhisperCppSourceBuildPhase phase)
    {
        return phase switch
        {
            WhisperCppSourceBuildPhase.Idle => "idle",
            WhisperCppSourceBuildPhase.Cloning => "cloning",
            WhisperCppSourceBuildPhase.Verifying => "verifying",
            WhisperCppSourceBuildPhase.Configuring => "configuring",
            WhisperCppSourceBuildPhase.Building => "building",
            WhisperCppSourceBuildPhase.SmokeTesting => "smokeTesting",
            WhisperCppSourceBuildPhase.Adopting => "adopting",
            WhisperCppSourceBuildPhase.Removing => "removing",
            WhisperCppSourceBuildPhase.Completed => "completed",
            WhisperCppSourceBuildPhase.Cancelled => "cancelled",
            WhisperCppSourceBuildPhase.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown whisper.cpp source-build phase.")
        };
    }
}

/// <summary>Why a start request did or did not begin a build.</summary>
public enum WhisperCppSourceBuildStartOutcome
{
    Started = 0,
    AlreadyRunning = 1,
    InsufficientDisk = 2,
    MissingPrerequisites = 3,

    /// <summary>Something is holding the transcription runtime; the operator sees <c>409 runtime-busy</c>.</summary>
    RuntimeBusy = 4
}

/// <summary>Why a remove request did or did not delete the managed runtime.</summary>
public enum WhisperCppSourceBuildRemoveOutcome
{
    Removed = 0,
    NotInstalled = 1,
    RuntimeBusy = 2
}

/// <summary>The answer to a start request, carrying whatever explains a refusal.</summary>
public sealed class WhisperCppSourceBuildStartResult
{
    public required WhisperCppSourceBuildStartOutcome Outcome { get; init; }

    public WhisperCppSourceBuildPrerequisiteReport? Prerequisites { get; init; }

    public WhisperRuntimeActivitySnapshot? Activity { get; init; }
}

/// <summary>The answer to a remove request.</summary>
public sealed class WhisperCppSourceBuildRemoveResult
{
    public required WhisperCppSourceBuildRemoveOutcome Outcome { get; init; }

    public WhisperRuntimeActivitySnapshot? Activity { get; init; }
}

/// <summary>
///     A poll-shaped snapshot of the running or last-finished build, including a bounded, sanitized tail of its log.
/// </summary>
public sealed class WhisperCppSourceBuildStatus
{
    public required WhisperCppSourceBuildPhase Phase { get; init; }

    public required bool IsRunning { get; init; }

    public required bool Terminal { get; init; }

    /// <summary>The retained tail; older lines are dropped as the build talks.</summary>
    public required IReadOnlyList<string> LogLines { get; init; }

    /// <summary>
    ///     Sequence number of <see cref="LogLines" />' first entry, so a client that polls can tell a dropped prefix from
    ///     a rewound one.
    /// </summary>
    public required long LogStartSequence { get; init; }

    /// <summary>Display-safe failure reason; never a path, URL or command line.</summary>
    public required string? SanitizedError { get; init; }

    public required WhisperCppSourceBuildDescriptor? CurrentBuild { get; init; }

    public required DateTimeOffset? StartedAtUtc { get; init; }

    public required DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>The running or last-finished build's id, when there has been one.</summary>
    public Guid? BuildId => CurrentBuild?.BuildId;
}

/// <summary>
///     Detached, single-flight, Linux-only source-build orchestration for a managed whisper.cpp runtime.
/// </summary>
/// <remarks>
///     There is no prebuilt Linux CUDA asset upstream, so this lane is the only way a Linux node gets GPU
///     transcription without a bring-your-own binary. A build mutates the managed runtime, so it takes the activity
///     gate's mutation reservation for its whole duration and is refused with
///     <see cref="WhisperCppSourceBuildStartOutcome.RuntimeBusy" /> while anything holds the runtime.
/// </remarks>
public interface IWhisperCppSourceBuildService
{
    /// <summary>Validates the request, checks prerequisites, and detaches the build. Returns as soon as it starts.</summary>
    Task<WhisperCppSourceBuildStartResult> StartAsync(WhisperCppSourceBuildRequest request, CancellationToken ct);

    /// <summary>Deletes the adopted managed runtime and its record. The in-app recovery from a fail-closed tombstone.</summary>
    Task<WhisperCppSourceBuildRemoveResult> RemoveAsync(CancellationToken ct);

    /// <summary>The current snapshot; never blocks on the build.</summary>
    WhisperCppSourceBuildStatus GetStatus();

    /// <summary>Requests cancellation of a running build. False when there is nothing to cancel.</summary>
    bool Cancel();

    /// <summary>
    ///     Reconciles an interrupted adoption and republishes the managed-runtime signal. Run at host start, where a
    ///     failure is fatal: continuing with an ambiguous journal could serve an unverified runtime.
    /// </summary>
    Task RecoverAsync(CancellationToken ct);

    /// <summary>Cancels and drains any in-flight build so the host can exit.</summary>
    Task ShutdownAsync(CancellationToken ct);
}
