namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;

/// <summary>
///     The resolved container runtime, its capability set and the daemon behind it.
/// </summary>
/// <param name="Status">
///     <c>Ready</c> | <c>DaemonUnreachable</c> | <c>PermissionDenied</c> | <c>ApiVersionTooOld</c> |
///     <c>DaemonIdentityChanged</c> | <c>NotConfigured</c> | <c>ProbeFailed</c>.
/// </param>
/// <param name="Available">
///     Read straight off <c>ContainerRuntimeResolution.Available</c> — the daemon answered THIS probe. The mapper
///     projects the member and never re-derives it from <paramref name="Status" />, so <c>PermissionDenied</c>,
///     <c>ApiVersionTooOld</c> and <c>DaemonIdentityChanged</c> arrive <c>available: true, ready: false</c>: reachable
///     but unusable, which is a different sentence for the operator than "no daemon". It is the single member the SPA
///     renders "Runtime unavailable" from, and it is why a missing daemon rewrites no instance row.
/// </param>
/// <param name="ForeignInstallContainers">
///     Owner-labelled containers whose install id belongs to another node data directory. Surfaced, never removed:
///     removal stays a manual operator act. It is 0 on an install preview, where nothing reconciles — the count is the
///     reconciler's observation, not a standing fact.
/// </param>
public sealed record ExternalAppRuntimeResponse(string Provider,
    string Status,
    bool Available,
    bool Ready,
    string Message,
    bool RequiresOperatorConfirmation,
    string? Endpoint,
    string? EndpointSource,
    ExternalAppDaemonView? ObservedDaemon,
    ExternalAppDaemonView? PinnedDaemon,
    ExternalAppCapabilitiesView Capabilities,
    int ForeignInstallContainers);

/// <summary>
///     One daemon, observed or pinned. <paramref name="DaemonId" /> and <paramref name="ServerVersion" /> are NULLABLE
///     because a probe that failed reports neither, and an empty-string identity would read as a daemon that answered.
///     The pinned view is built from the attestation's two members alone, so its <paramref name="ServerVersion" /> is
///     null by construction — the pin records who was approved, not what version answered.
/// </summary>
/// <param name="ConfirmedAtUtc">
///     Unix milliseconds, like every other time member here: a <c>DateTimeOffset</c> would hand the generated client a
///     string for this one field and a number for the rest.
/// </param>
public sealed record ExternalAppDaemonView(string? DaemonId, string? ServerVersion, string? Endpoint, long? ConfirmedAtUtc);

/// <summary>
///     Member names are exactly the manifest <c>requires[]</c> capability names. <c>GpuDevices</c> is always false.
/// </summary>
public sealed record ExternalAppCapabilitiesView(bool Containers,
    bool Networks,
    bool BindStorage,
    bool LoopbackPortPublishing,
    bool HealthChecks,
    bool RestartPolicies,
    bool Logs,
    bool ImagePull,
    bool GpuDevices);

/// <summary>
///     Body of <c>POST external-apps/runtime/refresh</c>. Names the daemon the operator was shown; it must equal the
///     CURRENTLY OBSERVED daemon id or the endpoint answers 400. Omitted re-probes without approving anything.
/// </summary>
public sealed record RefreshExternalAppRuntimeRequest
{
    /// <summary>The daemon id the operator read off the runtime panel, or null to re-probe without approving.</summary>
    public string? AcknowledgeDaemonId { get; init; }
}

/// <param name="FromBundledSeed">This document is the shipped seed, not a fetched one.</param>
/// <param name="RefreshFailureMessage">
///     Set on the POST when THIS refresh failed and the last-good document was returned;
///     <paramref name="LastRefreshFailure" /> is set when the MOST RECENT refresh failed, read off the snapshot. Two
///     members because "your click just failed" and "the cache is stale" are different sentences.
/// </param>
public sealed record ExternalAppCatalogResponse(int SchemaVersion,
    long GeneratedAtUtc,
    long FetchedAtUtc,
    bool FromBundledSeed,
    string? RefreshFailureMessage,
    string? LastRefreshFailure,
    IReadOnlyList<ExternalAppSummaryView> Applications);

/// <param name="InstalledInstanceId">
///     Set when this application already has an instance. V1 allows one, so the card links instead of offering install.
/// </param>
public sealed record ExternalAppSummaryView(string Id,
    int ManifestVersion,
    string DisplayName,
    string Summary,
    string Homepage,
    string License,
    string Trust,
    string TestedVersion,
    IReadOnlyList<string> Requires,
    ExternalAppPermissionsView Permissions,
    ExternalAppResourcesView Resources,
    Guid? InstalledInstanceId,
    string? InstalledStatus);

/// <summary>
///     The full manifest MINUS every <c>files[]</c> entry: those are base64 asset bodies the engine materialises, never
///     UI data. There is no user or uid member at either level — containers run as the image's default user, and the
///     boundary is the container's dropped capabilities, seccomp and loopback-only network.
/// </summary>
public sealed record ExternalAppManifestView(string Id,
    int ManifestVersion,
    string DisplayName,
    string Summary,
    string Description,
    string Homepage,
    string License,
    string Trust,
    string TestedVersion,
    IReadOnlyList<string> Requires,
    ExternalAppPermissionsView Permissions,
    ExternalAppResourcesView Resources,
    IReadOnlyList<ExternalAppServiceView> Services,
    IReadOnlyList<ExternalAppVariableView> Variables);

/// <summary>One container of an application, as the detail page renders it.</summary>
public sealed record ExternalAppServiceView(string Name,
    string Image,
    string ImageTag,
    IReadOnlyList<string>? Entrypoint,
    IReadOnlyList<string>? Command,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<ExternalAppPortView> Ports,
    IReadOnlyList<ExternalAppStorageView> Storage,
    bool HasHealthcheck,
    IReadOnlyList<ExternalAppDependencyView> DependsOn,
    IReadOnlyList<string> CapAdd,
    IReadOnlyList<string> ExtraHosts,
    bool ReadOnlyRootFilesystem);

public sealed record ExternalAppPortView(int ContainerPort, string Role, int? PreferredHostPort, string? OpenPath);

public sealed record ExternalAppStorageView(string Name, string ContainerPath);

public sealed record ExternalAppDependencyView(string Service, string Condition);

public sealed record ExternalAppPermissionsView(bool Internet, bool LocalNetwork, string HostFiles, string Gpu);

public sealed record ExternalAppResourcesView(int MinimumMemoryMb, int RecommendedMemoryMb, int CpuHint, int PidsLimit);

/// <summary>
///     The permissions an installed instance ACTUALLY holds, computed by <c>ExternalAppEffectivePermissions.From</c> and
///     carried on BOTH previews, so the panel renders a widening rather than the operator diffing two manifests by eye.
///     <paramref name="Services" /> is the per-service breakdown the server actually diffs, projected as a MAP keyed by
///     service name so the panel can name the service that gained a capability: aggregates alone hide a redistribution,
///     because moving a capability from service A to service B changes nothing at the union.
///     <para>
///         The four application-level aggregates — <paramref name="Capabilities" />,
///         <paramref name="WritableRootFilesystem" />, <paramref name="PublishedPorts" /> and
///         <paramref name="ExtraHosts" /> — complete the eight names of the widening vocabulary
///         (<c>ExternalAppEffectivePermissions.Vocabulary</c>), so the panel renders all eight from the wire instead
///         of deriving four of them client-side and disagreeing with the server about what an application may do.
///         They are DISPLAY ONLY: the verdict stays <c>addedPermissions</c>, which is the server's per-service diff.
///     </para>
/// </summary>
/// <param name="Capabilities">Every capability any service adds, ordinal-sorted so the render order is stable.</param>
/// <param name="WritableRootFilesystem">True when ANY service runs with a writable root filesystem.</param>
/// <param name="PublishedPorts">Every <c>service:containerPort</c> string of the map, ordinal-sorted.</param>
/// <param name="ExtraHosts">Every extra host any service declares, ordinal-sorted.</param>
public sealed record ExternalAppEffectivePermissionsView(bool Internet,
    string HostFiles,
    string Gpu,
    bool LocalNetwork,
    IReadOnlyDictionary<string, ExternalAppServicePermissionsView> Services,
    IReadOnlyList<string> Capabilities,
    bool WritableRootFilesystem,
    IReadOnlyList<string> PublishedPorts,
    IReadOnlyList<string> ExtraHosts);

/// <summary>
///     One service's own grants. Sets become arrays in declaration order; the mapper sorts nothing and adds nothing.
///     <paramref name="PublishedPorts" /> entries are <c>service:containerPort</c>, the same strings the diff compares.
/// </summary>
public sealed record ExternalAppServicePermissionsView(IReadOnlyList<string> Capabilities,
    bool WritableRootFilesystem,
    IReadOnlyList<string> PublishedPorts,
    IReadOnlyList<string> ExtraHosts);

/// <param name="Default">
///     NULL for a <c>secret</c> variable whatever the manifest says: a shipped secret default is never a wire value.
/// </param>
public sealed record ExternalAppVariableView(string Name,
    string Label,
    string? Description,
    string Type,
    bool Required,
    string? Default,
    IReadOnlyList<string>? AllowedValues,
    bool Advanced,
    ExternalAppVariableValidationView? Validation);

public sealed record ExternalAppVariableValidationView(int? MinLength, int? MaxLength, string? Pattern);

/// <param name="ManifestSha256">
///     SHA-256 of the canonical manifest JSON as served. Acceptance is bound to this fingerprint, not to the version
///     number: a catalog that republishes v3 with a wider <c>capAdd</c> produces a different sha, and the install 409s
///     <c>ExternalAppManifestChanged</c> instead of installing something the operator never read.
/// </param>
/// <param name="Variables">
///     EVERY declared variable, not just the required ones — <c>Required</c> is already a member, and required-only
///     would silently drop an application's optional settings at install.
/// </param>
/// <param name="BlockedReason">
///     Why <paramref name="CanInstall" /> is false, as the <c>ExternalAppBlockedReason</c> name: <c>GpuNotSupported</c>
///     | <c>RuntimeIncompatible</c> | <c>RuntimeUnavailable</c> | <c>InsufficientMemory</c> | <c>InsufficientDisk</c> |
///     <c>AlreadyInstalled</c> | <c>CatalogMissing</c> | <c>BridgeUnavailable</c>. Null when the install can proceed.
///     The install path cannot produce <c>CatalogMissing</c> and the update path cannot produce
///     <c>AlreadyInstalled</c>, but one closed vocabulary means the SPA ships eight labels once instead of two
///     overlapping sets.
/// </param>
public sealed record ExternalAppInstallPreview(string ApplicationId,
    int ManifestVersion,
    string ManifestSha256,
    bool CanInstall,
    string? BlockedReason,
    Guid? ExistingInstanceId,
    ExternalAppPermissionsView Permissions,
    ExternalAppEffectivePermissionsView EffectivePermissions,
    IReadOnlyList<ExternalAppVariableView> Variables,
    ExternalAppRuntimeResponse Runtime,
    IReadOnlyList<string> MissingCapabilities,
    ExternalAppResourceCheckView ResourceCheck);

/// <summary>
///     Body of <c>GET …/instances/{instanceId}/update-preview</c>. <paramref name="Variables" /> is every variable the
///     TARGET manifest declares; a variable that was <c>secret</c> in the installed snapshot and is plain in the target
///     arrives UNSET, because the stored value is discarded on update and never returned in plaintext.
/// </summary>
/// <param name="AddedPermissions">
///     The names this update ADDS, from the closed vocabulary. Empty means no widening and no acknowledgement needed.
/// </param>
/// <param name="TargetManifestVersion">
///     The version this update would move TO; <paramref name="CurrentManifestVersion" /> is what is installed. Named
///     for the target rather than bare <c>ManifestVersion</c> because a preview carrying both must not make the reader
///     guess which one a plain name meant.
/// </param>
/// <param name="ResourceVerdict">
///     The service's own resource verdict, projected. The install preview names the same shape <c>ResourceCheck</c>.
/// </param>
public sealed record ExternalAppUpdatePreview(string ApplicationId,
    Guid InstanceId,
    int CurrentManifestVersion,
    int TargetManifestVersion,
    string ManifestSha256,
    IReadOnlyList<ExternalAppVariableView> Variables,
    IReadOnlyDictionary<string, string> CurrentValues,
    IReadOnlyList<string> AddedPermissions,
    ExternalAppEffectivePermissionsView EffectivePermissions,
    ExternalAppResourceCheckView ResourceVerdict,
    bool CanUpdate,
    string? BlockedReason);

/// <param name="Message">
///     The sentence naming requested versus available, carried straight across, which the SPA shows rather than
///     composing it from the byte counts.
/// </param>
public sealed record ExternalAppResourceCheckView(bool Satisfied,
    string? FailureCategory,
    long RequiredMemoryBytes,
    long AvailableMemoryBytes,
    long RequiredDiskBytes,
    long AvailableDiskBytes,
    string Message);

/// <param name="Variables">Every declared variable name → its value, with <c>secret</c> values replaced by the mask sentinel.</param>
/// <param name="Version">The optimistic concurrency token a lifecycle or variables command must echo back.</param>
/// <param name="UpdateAvailable">
///     Computed by the SERVICE against the catalog's manifest version; false when <paramref name="CatalogMissing" />,
///     since an application that left the catalog cannot offer an update.
/// </param>
/// <param name="Manifest">
///     The INSTALLED snapshot, sanitised — <c>files[]</c> stripped, every <c>secret</c> variable's default nulled. The
///     detail page and the Settings tab read this and never the catalog: an instance installed at v2 must render what
///     it is running, not what the catalog now offers, and a <c>catalogMissing</c> instance must still render at all.
///     <c>testedVersion</c> reaches the client as <c>manifest.testedVersion</c>; there is no duplicate top-level member.
/// </param>
/// <param name="RuntimeOverride">Null means the node-wide selection applies.</param>
public sealed record ExternalAppInstanceView(Guid Id,
    string ApplicationId,
    string DisplayName,
    int ManifestVersion,
    string Status,
    string DesiredState,
    string? RuntimeOverride,
    string RuntimeProvider,
    ExternalAppManifestView Manifest,
    IReadOnlyList<ExternalAppPublishedPortView> PublishedPorts,
    IReadOnlyDictionary<string, string> Variables,
    string? FailureCategory,
    string? FailureSummary,
    bool UpdateAvailable,
    int? AvailableManifestVersion,
    bool CatalogMissing,
    long InstalledAtUtc,
    long? StartedAtUtc,
    long? StoppedAtUtc,
    long UpdatedAtUtc,
    long LastSequence,
    long Version);

/// <summary>
///     The admitted row as a lifecycle command left it — the snapshot taken before the operation runner starts, which
///     is why <paramref name="Status" /> carries <c>Starting</c>, <c>Stopping</c>, <c>Updating</c> and
///     <c>Resetting</c>. It is deliberately NOT the full <see cref="ExternalAppInstanceView" />: admission returns a
///     summary, and re-reading the instance to fill a manifest into a 202 body would return a row a concurrent
///     operation may already have moved.
/// </summary>
public sealed record ExternalAppInstanceSummaryView(Guid Id,
    string ApplicationId,
    string DisplayName,
    int ManifestVersion,
    string Status,
    string DesiredState,
    string? FailureCategory,
    string? FailureSummary,
    bool UpdateAvailable,
    int? AvailableManifestVersion,
    bool CatalogMissing,
    long UpdatedAtUtc,
    long Version);

/// <param name="OpenPath">
///     NULLABLE: a published port may be reachable without being an Open target — an application may publish a
///     <c>ui</c> port with no open path.
/// </param>
/// <param name="Url">
///     Composed here rather than in the SPA — the loopback host and the manifest's open path are both server facts —
///     and emitted ONLY for the port whose open path is non-null. At most one such port exists per application, so a
///     non-null url identifies the Open target unambiguously and the SPA selects on it rather than on "the first
///     published port".
/// </param>
public sealed record ExternalAppPublishedPortView(string Service, int ContainerPort, int HostPort, string? OpenPath, string? Url);

/// <summary>
///     FULL instance views, not summaries: a card shows the Open target and the runtime provider, and the store reads
///     every row whole regardless, so a summary here would only force one GET per card before the page could render.
///     The lifecycle 202s still answer <see cref="ExternalAppInstanceSummaryView" /> — an admitted row is a snapshot,
///     and re-reading it to fill in a manifest would return a row a concurrent operation may already have moved.
/// </summary>
public sealed record ListExternalAppInstancesResponse(IReadOnlyList<ExternalAppInstanceView> Items);

public sealed record InstallExternalAppRequest
{
    public required string ApplicationId { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>
    ///     The manifest the operator was shown, echoed from the install preview. Both members are required and both are
    ///     checked at admission; either one mismatching the manifest the catalog now serves is a 409
    ///     <c>ExternalAppManifestChanged</c>, and the SPA re-fetches the preview and re-shows step 1. The version alone
    ///     is not enough: a republished v3 keeps its number.
    /// </summary>
    public required int ManifestVersion { get; init; }

    /// <summary>The fingerprint of the manifest the operator was shown.</summary>
    public required string ManifestSha256 { get; init; }

    public required IReadOnlyDictionary<string, string> Variables { get; init; }

    /// <summary>The operator's explicit acceptance of the declared permissions; required true. Nothing is ambiently approved.</summary>
    public required bool AcceptPermissions { get; init; }
}

/// <summary>
///     Body of <c>POST …/update</c>. The fingerprint is echoed from the update preview and binds the acceptance to what
///     was read. <see cref="AcceptPermissions" /> acknowledges a WIDENING; the SERVER owns the diff, so it is a boolean
///     and never a list the client could get wrong. <see cref="Variables" /> is the target manifest's full declared set,
///     exactly as install sends it — the target may declare variables the installed snapshot never had, and one of them
///     may be required.
/// </summary>
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
///     UNINSTALL has its own <see cref="UninstallExternalAppRequest" />, because its version rides in the query and the
///     shape has to reach the generated client. A stale value is a 409 <c>ExternalAppVersionConflict</c>, echoed from
///     the instance's <c>version</c>. There are no idempotency keys in V1, so this is the whole mechanism.
/// </summary>
public sealed record ExternalAppInstanceCommandRequest
{
    public Guid InstanceId { get; init; }

    /// <summary>
    ///     <c>long?</c>, not <c>required long</c>. A required value type does not establish PRESENCE: an omitted member
    ///     binds to the default, so a command with no version would act at version 0 — a client that forgot the guard
    ///     would be treated as one that supplied it. Nullable plus a <c>NotNull</c> rule makes omission a 400
    ///     distinguishable from an explicit <c>0</c>. Endpoints read the value AFTER validation.
    /// </summary>
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
///     <paramref name="Items" /> is ASCENDING by sequence — oldest first, which is both the store's natural order and
///     what the History tab wants: it loads from 0 in pages of 200 and renders newest at the bottom, and "Load more"
///     advances <c>afterSequence</c> to the LAST returned sequence. There is no descending mode and no <c>before</c>
///     bound: one direction means the hub replay and the paged feed can share one store member without a reversal step
///     that could disagree.
/// </summary>
public sealed record ListExternalAppInstanceEventsResponse(IReadOnlyList<ExternalAppInstanceEventView> Items,
    long HighestSequence,
    bool HasMore);

public sealed record ExternalAppInstanceEventView(long Sequence, long AtUtc, string Kind, string? DetailJson);

public sealed record ExternalAppInstanceLogsRequest
{
    public Guid InstanceId { get; init; }

    /// <summary>Omitted means the application's primary service.</summary>
    public string? Service { get; init; }

    /// <summary>1..2000; above the cap is a 400, never a clamp.</summary>
    public int Tail { get; init; } = 500;
}

/// <param name="Text">
///     Raw container stdout and stderr, UNMASKED by design: the mapper masks engine-owned values only, and the engine
///     cannot tell an application's own echo of its password from any other line.
/// </param>
/// <param name="Truncated">Observed, not inferred — the daemon reported that bytes were discarded.</param>
public sealed record ExternalAppInstanceLogsResponse(string Service, string Text, int LineCount, bool Truncated);
