namespace XE_Local_AI_Engine.Client.Services.ExternalApps;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Containers;

/// <summary>Everything the node can do with an installed external application.</summary>
/// <remarks>
///     One service rather than one per verb: admission, the state machine and the per-instance gate are one decision
///     each, and splitting them would leave two objects both believing they hold the instance. Every mutating member is
///     TWO-PHASE — it admits synchronously (gate, row, transition table, version and manifest fingerprint, transient
///     status and its <c>*Requested</c> event), then the container work runs on a background operation owning the gate
///     lease — and each takes the <c>expectedVersion</c> the caller saw, so a retried reset cannot wipe new data.
/// </remarks>
public interface IExternalAppService
{
    /// <summary>Every installed instance, with its catalog-derived update availability.</summary>
    Task<IReadOnlyList<ExternalAppInstanceSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Every installed instance IN FULL, for the page that renders a card per instance.</summary>
    /// <remarks>
    ///     The store reads whole rows either way, so this costs one manifest deserialization per row over
    ///     <see cref="ListAsync" /> and saves the caller a <see cref="GetAsync" /> per card. Callers that need only
    ///     the identity and the status — the catalog join, for one — use <see cref="ListAsync" /> instead.
    /// </remarks>
    Task<IReadOnlyList<ExternalAppInstanceDetail>> ListDetailsAsync(CancellationToken cancellationToken = default);

    /// <summary>One instance in full. Throws <see cref="ExternalAppNotFoundException" /> when there is no such row.</summary>
    Task<ExternalAppInstanceDetail> GetAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     What installing <paramref name="applicationId" /> would do. Read-only, takes no gate and writes nothing;
    ///     it evaluates the same runtime, capability, GPU and resource inputs admission does.
    /// </summary>
    Task<InstallPreview> PreviewInstallAsync(string applicationId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     What updating this instance would do. Read-only and answers 200 even when the application has left the
    ///     catalog — a blocked preview is renderable information, where a mutating call in that state is not.
    /// </summary>
    Task<UpdatePreview> PreviewUpdateAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>Admits an install and returns the created row. The pull, the create and the start run afterwards.</summary>
    Task<ExternalAppInstanceSummary> InstallAsync(InstallCommand command, CancellationToken cancellationToken = default);

    /// <summary>Starts the instance, rebuilding its containers first when they are missing, stale or unverifiable.</summary>
    Task<ExternalAppInstanceSummary> StartAsync(Guid instanceId, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stops the instance's containers in reverse dependency order, keeping them and the network. Idempotent on an
    ///     already-stopped instance, where it re-asserts the desired state and writes no event.
    /// </summary>
    Task<ExternalAppInstanceSummary> StopAsync(Guid instanceId, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>Stop then start under one gate hold.</summary>
    Task<ExternalAppInstanceSummary> RestartAsync(Guid instanceId, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Wipes the instance's volumes and rebuilds it from the stored snapshot, restoring the desired state it had.
    ///     Data-destroying by design and one of only two members that deletes anything on disk.
    /// </summary>
    Task<ExternalAppInstanceSummary> ResetAsync(Guid instanceId, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>Removes the containers, the network, the rows and — best effort — the instance directory.</summary>
    Task<ExternalAppInstanceSummary> UninstallAsync(Guid instanceId, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>Rebuilds the instance against the catalog's current manifest.</summary>
    /// <remarks>
    ///     Admission runs every install precondition against the TARGET manifest before anything is stopped, so an
    ///     installed application cannot update into a manifest this version would refuse to install.
    /// </remarks>
    Task<ExternalAppInstanceSummary> UpdateAsync(Guid instanceId,
        long expectedVersion,
        UpdateCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Rewrites the stored variables. Stopped-only and fully synchronous.</summary>
    /// <remarks>
    ///     A created container's environment is immutable, so the values take effect on the next start, which rebuilds
    ///     because of the flag this sets. A value equal to <see cref="ExternalAppVariableMask.Value" /> means "keep
    ///     what is stored".
    /// </remarks>
    Task<ExternalAppInstanceDetail> ConfigureAsync(Guid instanceId,
        long expectedVersion,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Cancels the operation running on this instance. Accepted only on the three long transients a user can be
    ///     stuck behind; anything else, including an instance with nothing running, is an invalid transition.
    /// </summary>
    Task CancelAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>A page of the instance's event feed, ascending, strictly after <paramref name="afterSequence" />.</summary>
    Task<IReadOnlyList<ExternalAppInstanceEventSnapshot>> ListEventsAsync(Guid instanceId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     A bounded tail of one service's log. <paramref name="service" /> null selects the first service with a
    ///     published <c>ui</c> port, else the first in the manifest.
    /// </summary>
    /// <remarks>
    ///     Returned UNMASKED: a log is a diagnostic, and an application that prints its own secrets is telling its
    ///     operator something they need to see.
    /// </remarks>
    Task<ContainerLogSnapshot> ReadLogsAsync(Guid instanceId,
        string? service,
        int tail,
        CancellationToken cancellationToken = default);
}
