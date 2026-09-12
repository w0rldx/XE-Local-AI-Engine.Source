namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     An installed application as a reader sees it. <see cref="VariablesJson" /> and <see cref="BridgeToken" /> are
///     <b>decrypted</b> text, exactly as <see cref="IntegrationExecutionEventSnapshot.DetailJson" /> is — the
///     interceptors do the sealing — which is why this record prints nothing.
/// </summary>
public sealed record ExternalAppInstanceSnapshot(
    Guid Id,
    string ApplicationId,
    int ManifestVersion,
    string ManifestSnapshotJson,
    string DisplayName,
    ExternalAppInstanceStatus Status,
    ExternalAppDesiredState DesiredState,
    string? RuntimeOverride,
    string RuntimeProvider,
    string VariablesJson,
    string PublishedPortsJson,
    string StoragePath,
    ExternalAppFailureCategory? FailureCategory,
    string? FailureSummary,
    bool NeedsRecreate,
    long InstalledAtUtc,
    long? StartedAtUtc,
    long? StoppedAtUtc,
    long UpdatedAtUtc,
    long LastSequence,
    long Version,
    string? BridgeToken = null)
{
    // VariablesJson carries the user's own credentials in the clear and BridgeToken IS a credential, so the generated
    // ToString() would put an application's admin password — or its bridge access — into any log line that formats a
    // snapshot. Suppressing the printer makes that impossible rather than merely forbidden;
    // ExternalAppEncryptionTests asserts a known secret cannot appear.
    //
    // `private bool PrintMembers(StringBuilder)` and never `protected override`: on a sealed record whose base is
    // object the compiler expects exactly this signature, and the override form does not compile here. That shape is
    // also why the four analyzers are silenced rather than obeyed — every fix they suggest changes the signature into
    // one the compiler no longer recognises as the record's printer, silently restoring the ToString() that prints
    // the secret.
#pragma warning disable CA1822, S2325, S1172, IDE0060
    private bool PrintMembers(StringBuilder builder)
    {
        return false;
    }
#pragma warning restore CA1822, S2325, S1172, IDE0060
}

/// <summary>
///     One event on an instance's history. <see cref="DetailJson" /> is plaintext at rest by design: content-free by
///     contract and bounded at 4 KiB by the store, which is what lets the hub replay it as it stands.
/// </summary>
public sealed record ExternalAppInstanceEventSnapshot(
    Guid Id,
    Guid InstanceId,
    long Sequence,
    ExternalAppInstanceEventKind Kind,
    string? DetailJson,
    long OccurredAtUtc);

/// <summary>
///     One status compare-and-swap plus the event it mints. Every optional field is "leave it alone" when null, with
///     ONE exception: <see cref="FailureCategory" /> and <see cref="FailureSummary" /> are <b>assigned</b>, so a
///     successful transition clears a stale reason instead of inheriting it from a failed attempt — the same rule
///     <c>IntegrationExecutionStore.TryTerminalizeAsync</c> applies to a terminal write, and for the same reason.
///     <see cref="ClearFailure" /> is the explicit spelling of that clear; it wins over any values passed beside it.
/// </summary>
public sealed record ExternalAppStatusUpdate(
    Guid InstanceId,
    long ExpectedVersion,
    IReadOnlySet<ExternalAppInstanceStatus> ExpectedStatuses,
    ExternalAppInstanceStatus NewStatus,
    ExternalAppInstanceEventKind EventKind,
    string? EventDetailJson,
    long OccurredAtUtc,
    ExternalAppDesiredState? DesiredState = null,
    string? PublishedPortsJson = null,
    string? ManifestSnapshotJson = null,
    int? ManifestVersion = null,
    string? RuntimeProvider = null,
    long? StartedAtUtc = null,
    long? StoppedAtUtc = null,
    bool? NeedsRecreate = null,
    ExternalAppFailureCategory? FailureCategory = null,
    string? FailureSummary = null,
    bool ClearFailure = false);

/// <summary>
///     Everything an install admission writes: the row, at status <c>Installing</c> with
///     <c>DesiredState = Stopped</c>, and its first event at sequence 1.
///     <para>
///         <see cref="VariablesJson" /> and <see cref="BridgeToken" /> are PLAINTEXT text; the store encodes each to
///         UTF-8 and the save interceptor seals both. An application with no declared variables passes <c>{}</c>,
///         never null.
///     </para>
/// </summary>
public sealed record ExternalAppInstanceCreate(
    Guid Id,
    string ApplicationId,
    int ManifestVersion,
    string ManifestSnapshotJson,
    string DisplayName,
    string VariablesJson,
    string StoragePath,
    string RuntimeProvider,
    string? RuntimeOverride,
    long CreatedAtUtc,
    ExternalAppInstanceEventKind FirstEventKind,
    string? FirstEventDetailJson,
    string? BridgeToken = null)
{
    // Same reason and same shape as ExternalAppInstanceSnapshot's: this command carries the decrypted variables
    // and the freshly minted bridge token.
#pragma warning disable CA1822, S2325, S1172, IDE0060
    private bool PrintMembers(StringBuilder builder)
    {
        return false;
    }
#pragma warning restore CA1822, S2325, S1172, IDE0060
}

/// <summary>
///     What a write did. <c>Applied: false</c> means the compare-and-swap lost — no row, a stale version, or a status
///     outside the expected set — and NOTHING was written, not even the event; <see cref="Sequence" /> and
///     <see cref="Version" /> are then zero. A lost CAS is never an exception: the SERVICE decides what it means,
///     answering 409 on a user-initiated transition and ignoring it inside the reconciler, where the other writer's
///     verdict is the newer one.
/// </summary>
public sealed record ExternalAppStatusWriteResult(bool Applied, long Sequence, long Version);

/// <summary>
///     Persistence boundary for installed external applications and their event feed.
///     <para>
///         Unlike the integration family, this store MINTS the sequence — one event per status change, inside the same
///         compare-and-swap transaction — so the feed has neither holes nor reservations.
///     </para>
/// </summary>
public interface IExternalAppInstanceStore
{
    Task<ExternalAppInstanceSnapshot?> GetAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>Every instance, newest install first.</summary>
    Task<IReadOnlyList<ExternalAppInstanceSnapshot>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Every instance of one application. This is the read the install gate makes under the instance lease: a
    ///     non-empty result is the one-per-application refusal, which is why no unique index exists to enforce it.
    /// </summary>
    Task<IReadOnlyList<ExternalAppInstanceSnapshot>> ListByApplicationAsync(string applicationId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Inserts the row and its first event at sequence 1 in one save. Status is <c>Installing</c>,
    ///     <c>DesiredState</c> is <c>Stopped</c> and <c>Version</c> starts at 0 — the number the caller then hands the
    ///     runner as its first <c>expectedVersion</c>.
    /// </summary>
    Task<ExternalAppStatusWriteResult> CreateAsync(ExternalAppInstanceCreate command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Status CAS + the minted event, in ONE transaction. Returns <c>Applied: false</c> WITHOUT writing when the
    ///     row is missing, the version is stale or the current status is outside the command's expected set.
    /// </summary>
    Task<ExternalAppStatusWriteResult> UpdateStatusAsync(ExternalAppStatusUpdate command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Rewrites the configured variables under the version CAS and sets <c>NeedsRecreate</c>: a created container's
    ///     environment is immutable, so the running containers keep the old values until a start rebuilds them, and
    ///     that divergence has to be recorded rather than assumed. Writes no event and leaves the status alone.
    /// </summary>
    Task<bool> UpdateVariablesAsync(Guid instanceId, long expectedVersion, string variablesJson, long updatedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The update pipeline's single recovery boundary: the target snapshot, the target variables, the planned ports
    ///     and the target manifest version written together under one CAS, in one transaction. Writes NO event and
    ///     leaves <c>Status</c> and <c>DesiredState</c> alone — it exists so an update's row can never be half-written,
    ///     and an <c>Applied: false</c> aborts the update before any replacement container is started.
    /// </summary>
    /// <param name="bridgeToken">
    ///     PLAINTEXT, and written only when it is non-null: the backfill for a row installed before the bridge
    ///     existed, which carries none. It travels with the manifest and the variables because it belongs to the same
    ///     recovery boundary — the update recreates every container, so the token the replacements were given and the
    ///     token the row holds have to become true together or not at all. Null leaves the column as it stands, which
    ///     is what every ordinary update passes.
    /// </param>
    Task<ExternalAppStatusWriteResult> CommitUpdateAsync(Guid instanceId,
        long expectedVersion,
        string manifestSnapshotJson,
        string variablesJson,
        string publishedPortsJson,
        int manifestVersion,
        long updatedAtUtc,
        string? bridgeToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     A page of an instance's events, ordered by sequence, strictly after <paramref name="afterSequence" />. It is
    ///     the SINGLE event source: the events endpoint and the hub's subscribe both read it. A non-positive
    ///     <paramref name="limit" /> throws <see cref="ArgumentOutOfRangeException" />.
    /// </summary>
    Task<IReadOnlyList<ExternalAppInstanceEventSnapshot>> ListEventsAsync(Guid instanceId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes the events and the row in one transaction under the same version CAS. The events go explicitly:
    ///     the node connection leaves <c>PRAGMA foreign_keys</c> off, so the declared cascade never fires.
    /// </summary>
    Task<bool> DeleteAsync(Guid instanceId, long expectedVersion, CancellationToken cancellationToken = default);
}
