namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;

/// <summary>
///     The resolved container runtime, its capability set and the daemon behind it.
/// </summary>
public sealed class ExternalAppRuntimeResponse
{
    public required string Provider { get; init; }

    /// <summary>
    ///     <c>Ready</c> | <c>DaemonUnreachable</c> | <c>PermissionDenied</c> | <c>ApiVersionTooOld</c> |
    ///     <c>DaemonIdentityChanged</c> | <c>NotConfigured</c> | <c>ProbeFailed</c>.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    ///     Read straight off <c>ContainerRuntimeResolution.Available</c> — the daemon answered THIS probe.
    /// </summary>
    /// <remarks>
    ///     The mapper projects the member and never re-derives it from <see cref="Status" />, so
    ///     <c>PermissionDenied</c>, <c>ApiVersionTooOld</c> and <c>DaemonIdentityChanged</c> arrive
    ///     <c>available: true, ready: false</c>: reachable but unusable, which is a different sentence for the
    ///     operator than "no daemon". It is the single member the SPA renders "Runtime unavailable" from, and it is
    ///     why a missing daemon rewrites no instance row.
    /// </remarks>
    public required bool Available { get; init; }

    public required bool Ready { get; init; }

    public required string Message { get; init; }

    public required bool RequiresOperatorConfirmation { get; init; }

    public required string? Endpoint { get; init; }

    public required string? EndpointSource { get; init; }

    public required ExternalAppDaemonView? ObservedDaemon { get; init; }

    public required ExternalAppDaemonView? PinnedDaemon { get; init; }

    public required ExternalAppCapabilitiesView Capabilities { get; init; }

    /// <summary>
    ///     Owner-labelled containers whose install id belongs to another node data directory. Surfaced, never removed:
    ///     removal stays a manual operator act.
    /// </summary>
    /// <remarks>
    ///     It is 0 on an install preview, where nothing reconciles — the count is the reconciler's observation, not a
    ///     standing fact.
    /// </remarks>
    public required int ForeignInstallContainers { get; init; }
}

/// <summary>One daemon, observed or pinned.</summary>
/// <remarks>
///     <see cref="DaemonId" /> and <see cref="ServerVersion" /> are NULLABLE because a probe that failed reports
///     neither, and an empty-string identity would read as a daemon that answered. The pinned view is built from the
///     attestation's two members alone, so its <see cref="ServerVersion" /> is null by construction — the pin records
///     who was approved, not what version answered.
/// </remarks>
public sealed class ExternalAppDaemonView
{
    public required string? DaemonId { get; init; }

    public required string? ServerVersion { get; init; }

    public required string? Endpoint { get; init; }

    /// <summary>
    ///     Unix milliseconds, like every other time member here: a <c>DateTimeOffset</c> would hand the generated client a
    ///     string for this one field and a number for the rest.
    /// </summary>
    public required long? ConfirmedAtUtc { get; init; }
}

/// <summary>
///     Member names are exactly the manifest <c>requires[]</c> capability names. <c>GpuDevices</c> is always false.
/// </summary>
public sealed class ExternalAppCapabilitiesView
{
    public required bool Containers { get; init; }

    public required bool Networks { get; init; }

    public required bool BindStorage { get; init; }

    public required bool LoopbackPortPublishing { get; init; }

    public required bool HealthChecks { get; init; }

    public required bool RestartPolicies { get; init; }

    public required bool Logs { get; init; }

    public required bool ImagePull { get; init; }

    public required bool GpuDevices { get; init; }
}

/// <summary>
///     Body of <c>POST external-apps/runtime/refresh</c>. Names the daemon the operator was shown; it must equal the
///     CURRENTLY OBSERVED daemon id or the endpoint answers 400. Omitted re-probes without approving anything.
/// </summary>
public sealed record RefreshExternalAppRuntimeRequest
{
    /// <summary>The daemon id the operator read off the runtime panel, or null to re-probe without approving.</summary>
    public string? AcknowledgeDaemonId { get; init; }
}

public sealed class ExternalAppCatalogResponse
{
    public required int SchemaVersion { get; init; }

    public required long GeneratedAtUtc { get; init; }

    public required long FetchedAtUtc { get; init; }

    /// <summary>This document is the shipped seed, not a fetched one.</summary>
    public required bool FromBundledSeed { get; init; }

    /// <summary>Set on the POST when THIS refresh failed and the last-good document was returned.</summary>
    /// <remarks>
    ///     <see cref="LastRefreshFailure" /> is set when the MOST RECENT refresh failed, read off the snapshot. Two
    ///     members, because "your click just failed" and "the cache is stale" are different sentences.
    /// </remarks>
    public required string? RefreshFailureMessage { get; init; }

    public required string? LastRefreshFailure { get; init; }

    public required IReadOnlyList<ExternalAppSummaryView> Applications { get; init; }
}

public sealed class ExternalAppSummaryView
{
    public required string Id { get; init; }

    public required int ManifestVersion { get; init; }

    public required string DisplayName { get; init; }

    public required string Summary { get; init; }

    public required string Homepage { get; init; }

    public required string License { get; init; }

    public required string Trust { get; init; }

    public required string TestedVersion { get; init; }

    public required IReadOnlyList<string> Requires { get; init; }

    public required ExternalAppPermissionsView Permissions { get; init; }

    public required ExternalAppResourcesView Resources { get; init; }

    /// <summary>Set when this application already has an instance. V1 allows one, so the card links instead of offering install.</summary>
    public required Guid? InstalledInstanceId { get; init; }

    public required string? InstalledStatus { get; init; }
}

/// <summary>
///     The full manifest MINUS every <c>files[]</c> entry: those are base64 asset bodies the engine materialises,
///     never UI data.
/// </summary>
/// <remarks>
///     There is no user or uid member at either level — containers run as the image's default user, and the boundary
///     is the container's dropped capabilities, seccomp and loopback-only network.
/// </remarks>
public sealed class ExternalAppManifestView
{
    public required string Id { get; init; }

    public required int ManifestVersion { get; init; }

    public required string DisplayName { get; init; }

    public required string Summary { get; init; }

    public required string Description { get; init; }

    public required string Homepage { get; init; }

    public required string License { get; init; }

    public required string Trust { get; init; }

    public required string TestedVersion { get; init; }

    public required IReadOnlyList<string> Requires { get; init; }

    public required ExternalAppPermissionsView Permissions { get; init; }

    public required ExternalAppResourcesView Resources { get; init; }

    public required IReadOnlyList<ExternalAppServiceView> Services { get; init; }

    public required IReadOnlyList<ExternalAppVariableView> Variables { get; init; }
}

/// <summary>One container of an application, as the detail page renders it.</summary>
public sealed class ExternalAppServiceView
{
    public required string Name { get; init; }

    public required string Image { get; init; }

    public required string ImageTag { get; init; }

    public required IReadOnlyList<string>? Entrypoint { get; init; }

    public required IReadOnlyList<string>? Command { get; init; }

    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    public required IReadOnlyList<ExternalAppPortView> Ports { get; init; }

    public required IReadOnlyList<ExternalAppStorageView> Storage { get; init; }

    public required bool HasHealthcheck { get; init; }

    public required IReadOnlyList<ExternalAppDependencyView> DependsOn { get; init; }

    public required IReadOnlyList<string> CapAdd { get; init; }

    public required IReadOnlyList<string> ExtraHosts { get; init; }

    public required bool ReadOnlyRootFilesystem { get; init; }
}

public sealed class ExternalAppPortView
{
    public required int ContainerPort { get; init; }

    public required string Role { get; init; }

    public required int? PreferredHostPort { get; init; }

    public required string? OpenPath { get; init; }
}

public sealed class ExternalAppStorageView
{
    public required string Name { get; init; }

    public required string ContainerPath { get; init; }
}

public sealed class ExternalAppDependencyView
{
    public required string Service { get; init; }

    public required string Condition { get; init; }
}

public sealed class ExternalAppPermissionsView
{
    public required bool Internet { get; init; }

    public required bool LocalNetwork { get; init; }

    public required string HostFiles { get; init; }

    public required string Gpu { get; init; }
}

public sealed class ExternalAppResourcesView
{
    public required int MinimumMemoryMb { get; init; }

    public required int RecommendedMemoryMb { get; init; }

    public required int CpuHint { get; init; }

    public required int PidsLimit { get; init; }
}

/// <summary>
///     The permissions an installed instance ACTUALLY holds, computed by
///     <c>ExternalAppEffectivePermissions.From</c> and carried on BOTH previews, so the panel renders a widening
///     rather than the operator diffing two manifests by eye.
/// </summary>
/// <remarks>
///     <see cref="Services" /> is the per-service breakdown the server actually diffs, projected as a MAP keyed by service name so the panel can name the service that
///     gained a capability: aggregates alone hide a redistribution, since moving a capability from service A to B changes nothing at the union. The four
///     application-level aggregates (<see cref="Capabilities" />, <see cref="WritableRootFilesystem" />, <see cref="PublishedPorts" />, <see cref="ExtraHosts" />)
///     complete the eight names of the widening vocabulary (<c>ExternalAppEffectivePermissions.Vocabulary</c>), so the panel renders all eight from the wire rather
///     than deriving four client-side. They are DISPLAY ONLY: the verdict stays <c>addedPermissions</c>, the server's per-service diff.
/// </remarks>
public sealed class ExternalAppEffectivePermissionsView
{
    public required bool Internet { get; init; }

    public required string HostFiles { get; init; }

    public required string Gpu { get; init; }

    public required bool LocalNetwork { get; init; }

    public required IReadOnlyDictionary<string, ExternalAppServicePermissionsView> Services { get; init; }

    /// <summary>Every capability any service adds, ordinal-sorted so the render order is stable.</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>True when ANY service runs with a writable root filesystem.</summary>
    public required bool WritableRootFilesystem { get; init; }

    /// <summary>Every <c>service:containerPort</c> string of the map, ordinal-sorted.</summary>
    public required IReadOnlyList<string> PublishedPorts { get; init; }

    /// <summary>Every extra host any service declares, ordinal-sorted.</summary>
    public required IReadOnlyList<string> ExtraHosts { get; init; }
}

/// <summary>
///     One service's own grants. Sets become arrays in declaration order; the mapper sorts nothing and adds nothing.
///     <see cref="PublishedPorts" /> entries are <c>service:containerPort</c>, the same strings the diff compares.
/// </summary>
public sealed class ExternalAppServicePermissionsView
{
    public required IReadOnlyList<string> Capabilities { get; init; }

    public required bool WritableRootFilesystem { get; init; }

    public required IReadOnlyList<string> PublishedPorts { get; init; }

    public required IReadOnlyList<string> ExtraHosts { get; init; }
}

public sealed class ExternalAppVariableView
{
    public required string Name { get; init; }

    public required string Label { get; init; }

    public required string? Description { get; init; }

    public required string Type { get; init; }

    public required bool Required { get; init; }

    /// <summary>NULL for a <c>secret</c> variable whatever the manifest says: a shipped secret default is never a wire value.</summary>
    public required string? Default { get; init; }

    public required IReadOnlyList<string>? AllowedValues { get; init; }

    public required bool Advanced { get; init; }

    public required ExternalAppVariableValidationView? Validation { get; init; }
}

public sealed class ExternalAppVariableValidationView
{
    public required int? MinLength { get; init; }

    public required int? MaxLength { get; init; }

    public required string? Pattern { get; init; }
}

public sealed class ExternalAppInstallPreview
{
    public required string ApplicationId { get; init; }

    public required int ManifestVersion { get; init; }

    /// <summary>SHA-256 of the canonical manifest JSON as served.</summary>
    /// <remarks>
    ///     Acceptance is bound to this fingerprint, not to the version number: a catalog that republishes v3 with a
    ///     wider <c>capAdd</c> produces a different sha, and the install 409s <c>ExternalAppManifestChanged</c>
    ///     instead of installing something the operator never read.
    /// </remarks>
    public required string ManifestSha256 { get; init; }

    public required bool CanInstall { get; init; }

    /// <summary>
    ///     Why <see cref="CanInstall" /> is false, as the <c>ExternalAppBlockedReason</c> name; null when the install
    ///     can proceed.
    /// </summary>
    /// <remarks>
    ///     <c>GpuNotSupported</c> | <c>RuntimeIncompatible</c> | <c>RuntimeUnavailable</c> | <c>InsufficientMemory</c>
    ///     | <c>InsufficientDisk</c> | <c>AlreadyInstalled</c> | <c>CatalogMissing</c> | <c>BridgeUnavailable</c>. The
    ///     install path cannot produce <c>CatalogMissing</c> and the update path cannot produce
    ///     <c>AlreadyInstalled</c>, but one closed vocabulary means the SPA ships eight labels once instead of two
    ///     overlapping sets.
    /// </remarks>
    public required string? BlockedReason { get; init; }

    public required Guid? ExistingInstanceId { get; init; }

    public required ExternalAppPermissionsView Permissions { get; init; }

    public required ExternalAppEffectivePermissionsView EffectivePermissions { get; init; }

    /// <summary>
    ///     EVERY declared variable, not just the required ones — <c>Required</c> is already a member, and required-only
    ///     would silently drop an application's optional settings at install.
    /// </summary>
    public required IReadOnlyList<ExternalAppVariableView> Variables { get; init; }

    public required ExternalAppRuntimeResponse Runtime { get; init; }

    public required IReadOnlyList<string> MissingCapabilities { get; init; }

    public required ExternalAppResourceCheckView ResourceCheck { get; init; }
}

/// <summary>Body of <c>GET …/instances/{instanceId}/update-preview</c>.</summary>
/// <remarks>
///     <see cref="Variables" /> is every variable the TARGET manifest declares; a variable that was <c>secret</c> in
///     the installed snapshot and is plain in the target arrives UNSET, because the stored value is discarded on
///     update and never returned in plaintext.
/// </remarks>
public sealed class ExternalAppUpdatePreview
{
    public required string ApplicationId { get; init; }

    public required Guid InstanceId { get; init; }

    public required int CurrentManifestVersion { get; init; }

    /// <summary>
    ///     The version this update would move TO; <see cref="CurrentManifestVersion" /> is what is installed. Named
    ///     for the target rather than bare <c>ManifestVersion</c> because a preview carrying both must not make the reader
    ///     guess which one a plain name meant.
    /// </summary>
    public required int TargetManifestVersion { get; init; }

    public required string ManifestSha256 { get; init; }

    public required IReadOnlyList<ExternalAppVariableView> Variables { get; init; }

    public required IReadOnlyDictionary<string, string> CurrentValues { get; init; }

    /// <summary>The names this update ADDS, from the closed vocabulary. Empty means no widening and no acknowledgement needed.</summary>
    public required IReadOnlyList<string> AddedPermissions { get; init; }

    public required ExternalAppEffectivePermissionsView EffectivePermissions { get; init; }

    /// <summary>The service's own resource verdict, projected. The install preview names the same shape <c>ResourceCheck</c>.</summary>
    public required ExternalAppResourceCheckView ResourceVerdict { get; init; }

    public required bool CanUpdate { get; init; }

    public required string? BlockedReason { get; init; }
}

public sealed class ExternalAppResourceCheckView
{
    public required bool Satisfied { get; init; }

    public required string? FailureCategory { get; init; }

    public required long RequiredMemoryBytes { get; init; }

    public required long AvailableMemoryBytes { get; init; }

    public required long RequiredDiskBytes { get; init; }

    public required long AvailableDiskBytes { get; init; }

    /// <summary>
    ///     The sentence naming requested versus available, carried straight across, which the SPA shows rather than
    ///     composing it from the byte counts.
    /// </summary>
    public required string Message { get; init; }
}

public sealed class ExternalAppInstanceView
{
    public required Guid Id { get; init; }

    public required string ApplicationId { get; init; }

    public required string DisplayName { get; init; }

    public required int ManifestVersion { get; init; }

    public required string Status { get; init; }

    public required string DesiredState { get; init; }

    /// <summary>Null means the node-wide selection applies.</summary>
    public required string? RuntimeOverride { get; init; }

    public required string RuntimeProvider { get; init; }

    /// <summary>
    ///     The INSTALLED snapshot, sanitised — <c>files[]</c> stripped, every <c>secret</c> variable's default nulled.
    /// </summary>
    /// <remarks>
    ///     The detail page and the Settings tab read this and never the catalog: an instance installed at v2 must
    ///     render what it is running, not what the catalog now offers, and a <c>catalogMissing</c> instance must still
    ///     render at all. <c>testedVersion</c> reaches the client as <c>manifest.testedVersion</c>; there is no
    ///     duplicate top-level member.
    /// </remarks>
    public required ExternalAppManifestView Manifest { get; init; }

    public required IReadOnlyList<ExternalAppPublishedPortView> PublishedPorts { get; init; }

    /// <summary>Every declared variable name → its value, with <c>secret</c> values replaced by the mask sentinel.</summary>
    public required IReadOnlyDictionary<string, string> Variables { get; init; }

    public required string? FailureCategory { get; init; }

    public required string? FailureSummary { get; init; }

    /// <summary>
    ///     Computed by the SERVICE against the catalog's manifest version; false when <see cref="CatalogMissing" />,
    ///     since an application that left the catalog cannot offer an update.
    /// </summary>
    public required bool UpdateAvailable { get; init; }

    public required int? AvailableManifestVersion { get; init; }

    public required bool CatalogMissing { get; init; }

    public required long InstalledAtUtc { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? StoppedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required long LastSequence { get; init; }

    /// <summary>The optimistic concurrency token a lifecycle or variables command must echo back.</summary>
    public required long Version { get; init; }
}

/// <summary>
///     The admitted row as a lifecycle command left it — the snapshot taken before the operation runner starts, which
///     is why <see cref="Status" /> carries <c>Starting</c>, <c>Stopping</c>, <c>Updating</c> and <c>Resetting</c>.
/// </summary>
/// <remarks>
///     It is deliberately NOT the full <see cref="ExternalAppInstanceView" />: admission returns a summary, and
///     re-reading the instance to fill a manifest into a 202 body would return a row a concurrent operation may
///     already have moved.
/// </remarks>
public sealed class ExternalAppInstanceSummaryView
{
    public required Guid Id { get; init; }

    public required string ApplicationId { get; init; }

    public required string DisplayName { get; init; }

    public required int ManifestVersion { get; init; }

    public required string Status { get; init; }

    public required string DesiredState { get; init; }

    public required string? FailureCategory { get; init; }

    public required string? FailureSummary { get; init; }

    public required bool UpdateAvailable { get; init; }

    public required int? AvailableManifestVersion { get; init; }

    public required bool CatalogMissing { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required long Version { get; init; }
}

public sealed class ExternalAppPublishedPortView
{
    public required string Service { get; init; }

    public required int ContainerPort { get; init; }

    public required int HostPort { get; init; }

    /// <summary>
    ///     NULLABLE: a published port may be reachable without being an Open target — an application may publish a
    ///     <c>ui</c> port with no open path.
    /// </summary>
    public required string? OpenPath { get; init; }

    /// <summary>
    ///     Composed here rather than in the SPA — the loopback host and the manifest's open path are both server facts
    ///     — and emitted ONLY for the port whose open path is non-null.
    /// </summary>
    /// <remarks>
    ///     At most one such port exists per application, so a non-null url identifies the Open target unambiguously
    ///     and the SPA selects on it rather than on "the first published port".
    /// </remarks>
    public required string? Url { get; init; }
}

/// <summary>
///     FULL instance views, not summaries: a card shows the Open target and the runtime provider, and the store reads
///     every row whole regardless, so a summary here would only force one GET per card before the page could render.
/// </summary>
/// <remarks>
///     The lifecycle 202s still answer <see cref="ExternalAppInstanceSummaryView" /> — an admitted row is a snapshot,
///     and re-reading it to fill in a manifest would return a row a concurrent operation may already have moved.
/// </remarks>
public sealed class ListExternalAppInstancesResponse
{
    public required IReadOnlyList<ExternalAppInstanceView> Items { get; init; }
}

public sealed record InstallExternalAppRequest
{
    public required string ApplicationId { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>The manifest the operator was shown, echoed from the install preview.</summary>
    /// <remarks>
    ///     Both members are required and both are checked at admission; either one mismatching the manifest the
    ///     catalog now serves is a 409 <c>ExternalAppManifestChanged</c>, and the SPA re-fetches the preview and
    ///     re-shows step 1. The version alone is not enough: a republished v3 keeps its number.
    /// </remarks>
    public required int ManifestVersion { get; init; }

    /// <summary>The fingerprint of the manifest the operator was shown.</summary>
    public required string ManifestSha256 { get; init; }

    public required IReadOnlyDictionary<string, string> Variables { get; init; }

    /// <summary>The operator's explicit acceptance of the declared permissions; required true. Nothing is ambiently approved.</summary>
    public required bool AcceptPermissions { get; init; }
}

/// <summary>
///     Body of <c>POST …/update</c>; the fingerprint is echoed from the update preview and binds the acceptance to
///     what was read.
/// </summary>
/// <remarks>
///     <see cref="AcceptPermissions" /> acknowledges a WIDENING; the SERVER owns the diff, so it is a boolean and
///     never a list the client could get wrong. <see cref="Variables" /> is the target manifest's full declared set,
///     exactly as install sends it — the target may declare variables the installed snapshot never had, and one of
///     them may be required.
/// </remarks>
public sealed record UpdateExternalAppRequest
{
    public Guid InstanceId { get; init; }

    public required int ManifestVersion { get; init; }

    public required string ManifestSha256 { get; init; }

    public required bool AcceptPermissions { get; init; }

    public required IReadOnlyDictionary<string, string> Variables { get; init; }

    /// <summary>Nullable with a <c>NotNull</c> rule for the same reason as <see cref="ExternalAppInstanceCommandRequest.ExpectedVersion" />.</summary>
    public long? ExpectedVersion { get; init; }
}

public sealed record UpdateExternalAppVariablesRequest
{
    public Guid InstanceId { get; init; }

    /// <summary>
    ///     A FULL replacement of the declared set: send the mask sentinel to keep a stored secret, a real value to
    ///     replace it, an empty string to clear it.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Variables { get; init; }

    public long? ExpectedVersion { get; init; }
}

// Route and query parameters bind by name, so these property names ARE the wire names.
public sealed record ExternalAppApplicationRequest
{
    public string ApplicationId { get; init; } = string.Empty;
}

/// <summary>The bodyless request for get, update-preview and cancel — the three that carry no expected version.</summary>
public sealed record ExternalAppInstanceRequest
{
    public Guid InstanceId { get; init; }
}

/// <summary>
///     The four POST lifecycle verbs — start, stop, restart and reset — which send <c>expectedVersion</c> in the body.
/// </summary>
/// <remarks>
///     UNINSTALL has its own <see cref="UninstallExternalAppRequest" />, because its version rides in the query and
///     the shape has to reach the generated client. A stale value is a 409 <c>ExternalAppVersionConflict</c>, echoed
///     from the instance's <c>version</c>. There are no idempotency keys in V1, so this is the whole mechanism.
/// </remarks>
public sealed record ExternalAppInstanceCommandRequest
{
    public Guid InstanceId { get; init; }

    /// <summary>
    ///     <c>long?</c>, not <c>required long</c>, so an omission is a 400 distinguishable from an explicit <c>0</c>.
    /// </summary>
    /// <remarks>
    ///     A required value type does not establish PRESENCE: an omitted member binds to the default, so a command
    ///     with no version would act at version 0 — a client that forgot the guard would be treated as one that
    ///     supplied it. Nullable plus a <c>NotNull</c> rule is what makes the difference. Endpoints read the value
    ///     AFTER validation.
    /// </remarks>
    public long? ExpectedVersion { get; init; }
}

/// <summary>
///     Uninstall, whose <c>expectedVersion</c> is a QUERY parameter: a DELETE with a request body is a shape not every
///     HTTP stack will send, and intermediaries may drop it.
/// </summary>
/// <remarks>
///     A separate DTO rather than a second use of <see cref="ExternalAppInstanceCommandRequest" />. FastEndpoints binds
///     an unannotated member from the query happily enough, but the OpenAPI document then describes a REQUEST BODY, and
///     the generated client sends the version where this endpoint never reads it — a delete that 400s on every call the
///     client makes. <c>[QueryParam]</c> is what puts the parameter in the document; the attribute is the contract.
/// </remarks>
public sealed record UninstallExternalAppRequest
{
    public Guid InstanceId { get; init; }

    /// <summary>
    ///     Nullable with a <c>NotNull</c> rule, for the reason
    ///     <see cref="ExternalAppInstanceCommandRequest.ExpectedVersion" /> gives: an omitted <c>?expectedVersion=</c>
    ///     binds a value type to 0, and deleting at version 0 is exactly what the guard exists to prevent.
    /// </summary>
    [QueryParam]
    public long? ExpectedVersion { get; init; }
}

public sealed record ExternalAppInstanceEventFeedRequest
{
    public Guid InstanceId { get; init; }

    /// <summary>EXCLUSIVE lower bound. 0 replays from the beginning.</summary>
    public long AfterSequence { get; init; }

    public int Limit { get; init; } = 200;
}

/// <summary>
///     <see cref="Items" /> is ASCENDING by sequence — oldest first, which is both the store's natural order and what
///     the History tab wants.
/// </summary>
/// <remarks>
///     The tab loads from 0 in pages of 200 and renders newest at the bottom, and "Load more" advances
///     <c>afterSequence</c> to the LAST returned sequence. There is no descending mode and no <c>before</c> bound: one
///     direction means the hub replay and the paged feed can share one store member without a reversal step that
///     could disagree.
/// </remarks>
public sealed class ListExternalAppInstanceEventsResponse
{
    public required IReadOnlyList<ExternalAppInstanceEventView> Items { get; init; }

    public required long HighestSequence { get; init; }

    public required bool HasMore { get; init; }
}

public sealed class ExternalAppInstanceEventView
{
    public required long Sequence { get; init; }

    public required long AtUtc { get; init; }

    public required string Kind { get; init; }

    public required string? DetailJson { get; init; }
}

public sealed record ExternalAppInstanceLogsRequest
{
    public Guid InstanceId { get; init; }

    /// <summary>Omitted means the application's primary service.</summary>
    public string? Service { get; init; }

    /// <summary>1..2000; above the cap is a 400, never a clamp.</summary>
    public int Tail { get; init; } = 500;
}

public sealed class ExternalAppInstanceLogsResponse
{
    public required string Service { get; init; }

    /// <summary>
    ///     Raw container stdout and stderr, UNMASKED by design: the mapper masks engine-owned values only, and the engine
    ///     cannot tell an application's own echo of its password from any other line.
    /// </summary>
    public required string Text { get; init; }

    public required int LineCount { get; init; }

    /// <summary>Observed, not inferred — the daemon reported that bytes were discarded.</summary>
    public required bool Truncated { get; init; }
}
