namespace XE_Local_AI_Engine.Client.Services.ManagedPython;

/// <summary>Where one uv-managed feature environment stands (ADR 0016 §4). Reading it never provisions.</summary>
public enum ManagedPythonEnvironmentState
{
    NotProvisioned = 0,
    Provisioning = 1,
    Ready = 2,
    UpdateRequired = 3,
    RepairRequired = 4,
    Failed = 5,
    Unsupported = 6
}

/// <summary>
///     Names the fixed sentence behind <see cref="ManagedPythonEnvironmentStatus.Reason" />, so a client can render it in its own
///     language. <see cref="None" /> means there is no reason. The English text stays the fallback, and it can carry detail the code
///     does not, such as a provision failure's message.
/// </summary>
public enum ManagedPythonReasonCode
{
    None = 0,

    /// <summary>Compute runs on Linux x64 and Windows x64 only.</summary>
    PlatformUnsupported,

    /// <summary>The node kill-switch <c>Compute:Enabled</c> is off.</summary>
    ComputeDisabled,

    /// <summary>The sandbox cannot isolate the filesystem on this host, for a cause other than <see cref="ExecutionPreviewsDisabled" />.</summary>
    IsolationUnavailable,

    /// <summary>The AppContainer boundary is available but an operator has not turned on execution previews.</summary>
    ExecutionPreviewsDisabled,

    /// <summary>This or another process is provisioning the compute runtime.</summary>
    Provisioning,

    /// <summary>The compute runtime is being removed; the state still reads <c>Provisioning</c>.</summary>
    Removing,

    /// <summary>The last provision failed; with state <c>Ready</c> the previous runtime stays in service.</summary>
    ProvisionFailed,

    /// <summary>The pinned compute lockfile is missing from this installation.</summary>
    LockfileMissing,

    /// <summary>The compute runtime's Python interpreter is missing.</summary>
    InterpreterMissing,

    /// <summary>The compute runtime's state record is unreadable.</summary>
    RecordUnreadable,

    /// <summary>An interrupted provision left an incomplete compute runtime.</summary>
    IncompleteProvision,

    /// <summary>The compute runtime is out of date and is rebuilt on next use.</summary>
    UpdateRequired,

    /// <summary>An older build provisioned the compute runtime; it is rebuilt on next use.</summary>
    OlderBuild,

    /// <summary>Training runs on Linux x64 only.</summary>
    TrainingPlatformUnsupported,

    /// <summary>The training runtime is being installed.</summary>
    TrainingInstalling,

    /// <summary>The training runtime is being removed.</summary>
    TrainingRemoving,

    /// <summary>The last training runtime install failed; with state <c>Ready</c> the previous runtime stays in service.</summary>
    TrainingFailed,

    /// <summary>The training runtime's Python interpreter is missing.</summary>
    TrainingInterpreterMissing,

    /// <summary>The training runtime is out of date.</summary>
    TrainingOutOfDate
}

/// <summary>One feature environment's state, with a user-safe reason for every state but <c>Ready</c> and <c>NotProvisioned</c>.</summary>
public sealed class ManagedPythonEnvironmentStatus
{
    public required string ProfileId { get; init; }

    public required ManagedPythonEnvironmentState State { get; init; }

    public string? Reason { get; init; }

    /// <summary>The fixed sentence <see cref="Reason" /> carries; <see cref="ManagedPythonReasonCode.None" /> exactly when it is null.</summary>
    public ManagedPythonReasonCode ReasonCode { get; init; }

    /// <summary>What the installed environment was built from, when a readable record of it exists.</summary>
    public ManagedPythonEnvironmentIdentity? Installed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = [];
}

/// <summary>The shared toolchain store as this host sees it: the pinned uv and the CPython installs on disk.</summary>
public sealed class ManagedPythonToolchainStatus
{
    public required string UvVersion { get; init; }

    public required bool UvPresent { get; init; }

    public required IReadOnlyList<string> PythonInstalls { get; init; }
}

public sealed class ManagedPythonStatus
{
    public required ManagedPythonToolchainStatus Toolchain { get; init; }

    public required IReadOnlyList<ManagedPythonEnvironmentStatus> Environments { get; init; }
}

public enum ManagedPythonActionOutcome
{
    Completed = 0,
    Started = 1,
    Unsupported = 2,
    Busy = 3,
    Failed = 4
}

/// <summary>The result of a Compute repair or remove; <see cref="Message" /> is user-safe and set for every refusal.</summary>
public sealed class ManagedPythonActionResult
{
    public required ManagedPythonActionOutcome Outcome { get; init; }

    public string? Message { get; init; }
}
