namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Lifecycle of an <c>external_app_instances</c> row. Six of the ten are <b>transient</b> —
///     <see cref="Installing" />, <see cref="Starting" />, <see cref="Stopping" />, <see cref="Updating" />,
///     <see cref="Resetting" /> and <see cref="Uninstalling" /> — and accept no operation but Cancel.
/// </summary>
/// <remarks>
///     The transition table itself is owned by the application layer's <c>ExternalAppService</c> and mirrored in the
///     SPA, never by this store, which polices no transition and only compares against the expected set a caller
///     hands it.
/// </remarks>
public enum ExternalAppInstanceStatus
{
    /// <summary>The row exists and the install pipeline is pulling, creating and starting its containers.</summary>
    Installing,

    /// <summary>Installed, no container running, and that is what the user asked for.</summary>
    Stopped,

    /// <summary>A start or restart is rebuilding and starting the instance's containers.</summary>
    Starting,

    /// <summary>Every container of the instance is running and verified.</summary>
    Running,

    /// <summary>A stop is bringing the containers down in reverse dependency order.</summary>
    Stopping,

    /// <summary>An update is tearing the instance down and rebuilding it against the target manifest.</summary>
    Updating,

    /// <summary>A reset is wiping the instance's volumes and rebuilding it against the installed manifest.</summary>
    Resetting,

    /// <summary>An uninstall is removing the containers, the rows and the storage directory.</summary>
    Uninstalling,

    /// <summary>An operation failed; <c>FailureCategory</c> and <c>FailureSummary</c> say which and why.</summary>
    Failed,

    /// <summary>
    ///     A container the engine expected to be running is missing or exited — a <c>docker stop</c>, an OOM kill or a
    ///     daemon restart. Distinct from <see cref="Failed" />, which is the engine's own operation failing.
    /// </summary>
    StoppedUnexpectedly
}

/// <summary>
///     What the user last asked for, independent of where the instance currently <b>is</b>. It is what the boot
///     reconciler compares the daemon against, and what decides the Docker restart policy the engine creates the
///     containers with.
/// </summary>
public enum ExternalAppDesiredState
{
    /// <summary>The instance should not be running; its containers are created with restart policy <c>no</c>.</summary>
    Stopped,

    /// <summary>The instance should be running; its containers are created with restart policy <c>unless-stopped</c>.</summary>
    Running
}

/// <summary>
///     Why an instance is in <see cref="ExternalAppInstanceStatus.Failed" /> or
///     <see cref="ExternalAppInstanceStatus.StoppedUnexpectedly" />. A closed vocabulary keyed on the failing
///     <b>phase</b> rather than on daemon prose, which changes between engine releases; the accompanying
///     <c>FailureSummary</c> elaborates it in at most 512 content-free characters.
/// </summary>
public enum ExternalAppFailureCategory
{
    /// <summary>No container runtime could be reached, or the daemon refused the request outright.</summary>
    RuntimeUnavailable,

    /// <summary>The runtime is reachable but lacks a capability the manifest requires.</summary>
    RuntimeIncompatible,

    /// <summary>The manifest asks for a GPU and the runtime cannot provide one.</summary>
    GpuNotSupported,

    /// <summary>The admission gate measured less free memory than the manifest's minimum plus its headroom.</summary>
    InsufficientMemory,

    /// <summary>The admission gate measured less free disk on the instance volume than the install needs.</summary>
    InsufficientDisk,

    /// <summary>An image could not be pulled at its pinned digest.</summary>
    ImagePullFailed,

    /// <summary>A required variable has no value, or a manifest token could not be resolved.</summary>
    ConfigurationMissing,

    /// <summary>A container failed the engine's own policy verification, before or after start.</summary>
    PolicyViolation,

    /// <summary>A host port the plan chose is no longer bindable on loopback.</summary>
    PortUnavailable,

    /// <summary>A service did not become healthy inside the readiness budget, or exited while waiting.</summary>
    HealthCheckFailed,

    /// <summary>A container the engine expected to be running was found missing or exited.</summary>
    StoppedUnexpectedly,

    /// <summary>Creating, materialising, hashing or deleting something under the instance directory failed.</summary>
    StorageError,

    /// <summary>Anything the translator cannot place, including "the engine restarted mid-operation".</summary>
    Unknown
}

/// <summary>
///     What one <c>external_app_instance_events</c> row records. The list is the brief's, verbatim: a request kind for
///     each user-initiated operation and a completion kind for each outcome, so the feed reads as a history rather than
///     as a status log.
/// </summary>
public enum ExternalAppInstanceEventKind
{
    /// <summary>The install pipeline completed and the instance exists.</summary>
    Installed,

    /// <summary>A start was admitted.</summary>
    StartRequested,

    /// <summary>Every container is running and verified.</summary>
    Started,

    /// <summary>A stop was admitted.</summary>
    StopRequested,

    /// <summary>Every container is down.</summary>
    Stopped,

    /// <summary>A restart completed.</summary>
    Restarted,

    /// <summary>An update was admitted.</summary>
    UpdateRequested,

    /// <summary>An update completed against the target manifest.</summary>
    Updated,

    /// <summary>A reset was admitted.</summary>
    ResetRequested,

    /// <summary>A reset completed; the instance's volumes were wiped and rebuilt.</summary>
    Reset,

    /// <summary>An uninstall was admitted.</summary>
    UninstallRequested,

    /// <summary>The instance's containers, rows and storage are gone.</summary>
    Uninstalled,

    /// <summary>An operation failed; the row carries the category and the content-free summary.</summary>
    Failed,

    /// <summary>The boot reconciler found the instance running as expected and adopted it.</summary>
    RestoredOnBoot,

    /// <summary>A container the engine expected to be running was found missing or exited.</summary>
    StoppedUnexpectedly,

    /// <summary>
    ///     The user accepted the manifest's declared permissions. It is the first event of every instance, at sequence
    ///     1, written by the install admission itself.
    /// </summary>
    PermissionAccepted
}
