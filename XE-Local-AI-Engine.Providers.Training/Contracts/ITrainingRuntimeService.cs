namespace XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     Phases of one runtime install (or removal). Ordered as they occur; <see cref="Ready" />,
///     <see cref="Failed" /> and <see cref="Idle" /> are the resting states.
/// </summary>
public enum TrainingRuntimePhase
{
    Idle = 0,
    AcquiringUv = 1,
    ProvisioningPython = 2,
    InstallingPackages = 3,
    Verifying = 4,
    Ready = 5,
    Failed = 6,
    Removing = 7
}

/// <summary>Why an install request did not start. A refusal, not an exception — these are 409/412-shaped outcomes.</summary>
public enum TrainingRuntimeInstallOutcome
{
    Started = 0,
    AlreadyRunning = 1,
    InsufficientDisk = 2,
    MissingPrerequisites = 3
}

public sealed class TrainingRuntimeInstallResult
{
    public required TrainingRuntimeInstallOutcome Outcome { get; init; }

    public TrainingRuntimePrerequisiteReport? Prerequisites { get; init; }
}

/// <summary>
///     What <c>probe.py</c> reported from inside the provisioned venv. Every version field is optional because the probe
///     is deliberately written to emit a partial report when an import fails rather than to die with a traceback — a
///     runtime that is merely missing a package must produce an actionable message, not an empty one.
/// </summary>
public sealed class TrainingRuntimeProbeReport
{
    public required int ContractVersion { get; init; }

    public required bool Ready { get; init; }

    public required string? PythonVersion { get; init; }

    public required string? TorchVersion { get; init; }

    public required string? UnslothVersion { get; init; }

    public required string? BitsAndBytesVersion { get; init; }

    public required bool CudaAvailable { get; init; }

    public required string? DeviceName { get; init; }

    public required string? DeviceCapability { get; init; }

    public required IReadOnlyDictionary<string, string> Errors { get; init; }
}

/// <summary>The persisted record of what is installed, read back on every status call.</summary>
public sealed record InstalledTrainingRuntimeState(
    string UvVersion,
    string UvSha256,
    string PythonVersion,
    string LockfileSha256,
    int ContractVersion,
    DateTimeOffset InstalledAtUtc,
    string? TorchVersion = null,
    string? UnslothVersion = null,
    string? DeviceName = null);

public sealed class TrainingRuntimeStatus
{
    public required TrainingRuntimePhase Phase { get; init; }

    public required bool IsRunning { get; init; }

    public required bool Terminal { get; init; }

    public required IReadOnlyList<string> LogLines { get; init; }

    public required long LogStartSequence { get; init; }

    public required string? SanitizedError { get; init; }

    public required InstalledTrainingRuntimeState? Installed { get; init; }

    public required DateTimeOffset? StartedAtUtc { get; init; }

    public required DateTimeOffset? CompletedAtUtc { get; init; }
}

/// <summary>
///     Single-flight provisioning of the uv-managed Python training runtime (ADR 0005 decision 1). Linux-only, machine
///     -global, and strictly lockfile-driven: there is no floating resolve and no fallback to a system interpreter, so a
///     failed install means the Training feature is unavailable rather than silently degraded.
/// </summary>
public interface ITrainingRuntimeService
{
    Task<TrainingRuntimeInstallResult> InstallAsync(CancellationToken ct);

    TrainingRuntimeStatus GetStatus();

    /// <summary>Removes the installed runtime and its state record. Refused while an install is in flight.</summary>
    Task<bool> RemoveAsync(CancellationToken ct);

    /// <summary>Requests cancellation of an in-flight install. Returns false when nothing is running.</summary>
    bool Cancel();

    /// <summary>
    ///     Resolves the interpreter of the adopted venv, or <see langword="null" /> when no runtime is installed. This is
    ///     the seam the run executor launches <c>train.py</c> through.
    /// </summary>
    string? ResolveInterpreterPath();
}

public static class TrainingRuntimeHubEvents
{
    public const string StatusChanged = "trainingRuntime.statusChanged";
}

/// <summary>
///     One status push. Carries only the lines appended since the last push plus their starting sequence, so a client
///     that reconnects can splice its local log at a known offset instead of re-rendering the whole ring.
/// </summary>
public sealed class TrainingRuntimeStatusHubEvent
{
    public required string Phase { get; init; }

    public required IReadOnlyList<string> AppendedLogLines { get; init; }

    public required long AppendedLogStartSequence { get; init; }

    public required bool Terminal { get; init; }

    public required string? SanitizedError { get; init; }
}

/// <summary>
///     Transport seam for <see cref="ITrainingRuntimeService" />. The provider stays transport-agnostic; the Client host
///     supplies the SignalR-backed implementation and projects this into its own stable wire shape.
/// </summary>
public interface ITrainingRuntimeEventPublisher
{
    Task PublishStatusAsync(TrainingRuntimeStatusHubEvent statusEvent, CancellationToken cancellationToken = default);
}
