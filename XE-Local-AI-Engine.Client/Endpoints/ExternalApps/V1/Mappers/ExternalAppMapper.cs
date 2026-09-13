namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;

using System.Globalization;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

/// <summary>
///     Projects the External Apps service results onto the endpoint DTOs. It PROJECTS and nothing else: the service
///     deserializes the stored manifest, computes update availability, the effective permissions and the blocked
///     reason, and masks the secret variables. A mapper that parsed JSON or re-derived a verdict would be a service in
///     disguise, and the one it disagreed with would be the one the operator accepted.
/// </summary>
internal static class ExternalAppMapper
{
    /// <summary>The variable type whose value never leaves the node and whose declared default is never a wire value.</summary>
    private const string SecretVariableType = "secret";

    private const string LoopbackHost = "http://127.0.0.1:";

    /// <summary>
    ///     <paramref name="foreignInstallContainers" /> is the reconciler's observation and is 0 wherever nothing
    ///     reconciled — an install preview, for instance. <c>Available</c> is copied off the resolution rather than
    ///     re-derived from the status, so "the daemon answered but we cannot use it" keeps its own sentence.
    ///     <para>
    ///         <c>ObservedDaemon</c> is NULL when the probe reached no daemon, rather than a view whose every member is
    ///         null: the DTO declares it nullable, and an object present with a null id reads as a daemon that answered.
    ///     </para>
    /// </summary>
    public static ExternalAppRuntimeResponse ToRuntimeResponse(ContainerRuntimeResolution resolution, int foreignInstallContainers)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        var daemon = resolution.Daemon;

        return new ExternalAppRuntimeResponse(resolution.Provider,
            resolution.Status.ToString(),
            resolution.Available,
            resolution.Ready,
            resolution.Message,
            resolution.RequiresOperatorConfirmation,
            daemon.Endpoint,
            daemon.EndpointSource.ToString(),
            daemon.DaemonId is null
                ? null
                : new ExternalAppDaemonView(daemon.DaemonId, daemon.ServerVersion, daemon.Endpoint, ConfirmedAtUtc: null),
            daemon.PinnedDaemonId is null
                ? null
                : new ExternalAppDaemonView(daemon.PinnedDaemonId,
                    ServerVersion: null,
                    Endpoint: null,
                    daemon.PinnedDaemonConfirmedAtUtc?.ToUnixTimeMilliseconds()),
            ToCapabilitiesView(resolution.Capabilities),
            foreignInstallContainers);
    }

    /// <summary>
    ///     The catalog cards. <paramref name="installedByApplicationId" /> is the join the ENDPOINT performs against
    ///     the instance list: the mapper cannot reach the store, and leaving the join to the SPA is the second
    ///     round-trip the card exists to avoid.
    /// </summary>
    public static ExternalAppCatalogResponse ToCatalogResponse(ExternalAppCatalogSnapshot snapshot,
        string? refreshFailureMessage,
        IReadOnlyDictionary<string, ExternalAppInstanceSummary> installedByApplicationId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(installedByApplicationId);

        var applications = new List<ExternalAppSummaryView>(snapshot.Document.Applications.Count);
        foreach (var manifest in snapshot.Document.Applications)
        {
            applications.Add(ToSummaryView(manifest, installedByApplicationId));
        }

        return new ExternalAppCatalogResponse(snapshot.Document.SchemaVersion,
            ToUnixMilliseconds(snapshot.Document.GeneratedAtUtc),
            snapshot.FetchedAtUtc?.ToUnixTimeMilliseconds() ?? 0L,
            snapshot.Source == ExternalAppCatalogSource.Bundled,
            refreshFailureMessage,
            snapshot.LastRefreshFailure,
            applications);
    }

    /// <summary>One catalog card, carrying the instance join when this application already has one.</summary>
    public static ExternalAppSummaryView ToSummaryView(ApplicationManifest manifest,
        IReadOnlyDictionary<string, ExternalAppInstanceSummary> installedByApplicationId)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(installedByApplicationId);

        _ = installedByApplicationId.TryGetValue(manifest.Id, out var installed);

        return new ExternalAppSummaryView(manifest.Id,
            manifest.ManifestVersion,
            manifest.DisplayName,
            manifest.Summary,
            manifest.Homepage,
            manifest.License,
            manifest.Trust,
            manifest.TestedVersion,
            manifest.Requires,
            ToPermissionsView(manifest.Permissions),
            ToResourcesView(manifest.Resources),
            installed?.Id,
            installed?.Status.ToString());
    }

    /// <summary>
    ///     The manifest as the wire carries it: every <c>files[]</c> asset body dropped, and every <c>secret</c>
    ///     variable's declared default nulled. Both are removals, so a member added to the catalog contract cannot
    ///     leak through this by default — it has to be mapped in.
    /// </summary>
    public static ExternalAppManifestView ToManifestView(ApplicationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var services = new List<ExternalAppServiceView>(manifest.Services.Count);
        foreach (var service in manifest.Services)
        {
            var ports = new List<ExternalAppPortView>(service.Ports.Count);
            foreach (var port in service.Ports)
            {
                ports.Add(new ExternalAppPortView(port.ContainerPort, port.Role, port.PreferredHostPort, port.OpenPath));
            }

            var storage = new List<ExternalAppStorageView>(service.Storage.Count);
            foreach (var entry in service.Storage)
            {
                storage.Add(new ExternalAppStorageView(entry.Name, entry.ContainerPath));
            }

            var dependsOn = new List<ExternalAppDependencyView>(service.DependsOn.Count);
            foreach (var dependency in service.DependsOn)
            {
                dependsOn.Add(new ExternalAppDependencyView(dependency.Service, dependency.Condition));
            }

            services.Add(new ExternalAppServiceView(service.Name,
                service.Image,
                service.ImageTag,
                service.Entrypoint,
                service.Command,
                service.Environment,
                ports,
                storage,
                service.Healthcheck is not null,
                dependsOn,
                service.CapAdd,
                service.ExtraHosts,
                service.ReadOnlyRootFilesystem));
        }

        return new ExternalAppManifestView(manifest.Id,
            manifest.ManifestVersion,
            manifest.DisplayName,
            manifest.Summary,
            manifest.Description,
            manifest.Homepage,
            manifest.License,
            manifest.Trust,
            manifest.TestedVersion,
            manifest.Requires,
            ToPermissionsView(manifest.Permissions),
            ToResourcesView(manifest.Resources),
            services,
            ToVariableViews(manifest.Variables));
    }

    /// <summary>
    ///     The install disclosure. <paramref name="runtime" /> is built by the endpoint from the preview's own
    ///     resolution with a zero foreign-container count: nothing reconciles on a preview, so the count would be a
    ///     standing fact reported as an observation.
    /// </summary>
    public static ExternalAppInstallPreview ToPreview(InstallPreview preview, ExternalAppRuntimeResponse runtime)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(runtime);

        return new ExternalAppInstallPreview(preview.ApplicationId,
            preview.ManifestVersion,
            preview.ManifestSha256,
            preview.CanInstall,
            preview.BlockedReason?.ToString(),
            preview.ExistingInstanceId,
            ToPermissionsView(preview.Permissions),
            ToEffectivePermissionsView(preview.EffectivePermissions),
            ToVariableViews(preview.Variables),
            runtime,
            preview.MissingCapabilities,
            ToResourceCheckView(preview.Resources));
    }

    /// <summary>
    ///     The update disclosure — a member-for-member projection, so a rename on the service side is a compile error
    ///     here rather than a silently null wire field. A catalog-missing preview needs no branch: the service already
    ///     filled <c>CanUpdate: false</c> and the reason.
    /// </summary>
    public static ExternalAppUpdatePreview ToUpdatePreview(UpdatePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        return new ExternalAppUpdatePreview(preview.ApplicationId,
            preview.InstanceId,
            preview.CurrentManifestVersion,
            preview.TargetManifestVersion,
            preview.ManifestSha256,
            ToVariableViews(preview.Variables),
            preview.CurrentValues,
            preview.AddedPermissions,
            ToEffectivePermissionsView(preview.EffectivePermissions),
            ToResourceCheckView(preview.ResourceVerdict),
            preview.CanUpdate,
            preview.BlockedReason?.ToString());
    }

    /// <summary>
    ///     One installed instance in full. The variables are the service's already-masked map — the masking happens
    ///     where the stored values are read, so there is one place that decides and no second mask constant here.
    /// </summary>
    public static ExternalAppInstanceView ToView(ExternalAppInstanceDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var summary = detail.Summary;

        return new ExternalAppInstanceView(summary.Id,
            summary.ApplicationId,
            summary.DisplayName,
            summary.ManifestVersion,
            summary.Status.ToString(),
            summary.DesiredState.ToString(),
            detail.RuntimeOverride,
            detail.RuntimeProvider,
            ToManifestView(detail.Manifest),
            ToPublishedPortViews(detail.Manifest, detail.PublishedPorts),
            detail.MaskedVariables,
            summary.FailureCategory?.ToString(),
            summary.FailureSummary,
            summary.UpdateAvailable,
            summary.AvailableManifestVersion,
            summary.CatalogMissing,
            detail.InstalledAtUtc,
            detail.StartedAtUtc,
            detail.StoppedAtUtc,
            summary.UpdatedAtUtc,
            detail.LastSequence,
            summary.Version);
    }

    /// <summary>The admitted row a lifecycle command answers with. The instance LIST renders full views instead.</summary>
    public static ExternalAppInstanceSummaryView ToSummaryView(ExternalAppInstanceSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new ExternalAppInstanceSummaryView(summary.Id,
            summary.ApplicationId,
            summary.DisplayName,
            summary.ManifestVersion,
            summary.Status.ToString(),
            summary.DesiredState.ToString(),
            summary.FailureCategory?.ToString(),
            summary.FailureSummary,
            summary.UpdateAvailable,
            summary.AvailableManifestVersion,
            summary.CatalogMissing,
            summary.UpdatedAtUtc,
            summary.Version);
    }

    /// <summary>One row of the append-only event feed, shared by the paged read and the hub replay.</summary>
    public static ExternalAppInstanceEventView ToEventView(ExternalAppInstanceEventSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new ExternalAppInstanceEventView(snapshot.Sequence, snapshot.OccurredAtUtc, snapshot.Kind.ToString(), snapshot.DetailJson);
    }

    /// <summary>
    ///     Projects the per-service grant map whole, and the four application-level aggregates the widening vocabulary
    ///     names, so the panel reads all eight names off the wire rather than deriving half of them.
    ///     <para>
    ///         The aggregates are DISPLAY ONLY and change no verdict: the diff the server refuses an update with stays
    ///         per service (a capability moved from one service to another is a widening the union cannot see). They
    ///         are ordinal-sorted because a map's enumeration order is not a contract and a card must not reshuffle
    ///         between two reads of the same manifest.
    ///     </para>
    /// </summary>
    public static ExternalAppEffectivePermissionsView ToEffectivePermissionsView(ExternalAppEffectivePermissions permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        var services = new Dictionary<string, ExternalAppServicePermissionsView>(permissions.Services.Count, StringComparer.Ordinal);
        var capabilities = new SortedSet<string>(StringComparer.Ordinal);
        var publishedPorts = new SortedSet<string>(StringComparer.Ordinal);
        var extraHosts = new SortedSet<string>(StringComparer.Ordinal);
        var writableRootFilesystem = false;

        foreach (var entry in permissions.Services)
        {
            services[entry.Key] = new ExternalAppServicePermissionsView([.. entry.Value.Capabilities],
                entry.Value.WritableRootFilesystem,
                [.. entry.Value.PublishedPorts],
                [.. entry.Value.ExtraHosts]);

            capabilities.UnionWith(entry.Value.Capabilities);
            publishedPorts.UnionWith(entry.Value.PublishedPorts);
            extraHosts.UnionWith(entry.Value.ExtraHosts);
            writableRootFilesystem |= entry.Value.WritableRootFilesystem;
        }

        return new ExternalAppEffectivePermissionsView(permissions.Internet,
            permissions.HostFiles,
            permissions.Gpu,
            permissions.LocalNetwork,
            services,
            [.. capabilities],
            writableRootFilesystem,
            [.. publishedPorts],
            [.. extraHosts]);
    }

    private static ExternalAppResourceCheckView ToResourceCheckView(ExternalAppResourceVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return new ExternalAppResourceCheckView(verdict.Satisfied,
            verdict.FailureCategory?.ToString(),
            verdict.RequiredMemoryBytes,
            verdict.AvailableMemoryBytes,
            verdict.RequiredDiskBytes,
            verdict.AvailableDiskBytes,
            verdict.Message);
    }

    private static ExternalAppCapabilitiesView ToCapabilitiesView(ContainerRuntimeCapabilities capabilities)
    {
        return new ExternalAppCapabilitiesView(capabilities.Containers,
            capabilities.Networks,
            capabilities.BindStorage,
            capabilities.LoopbackPortPublishing,
            capabilities.HealthChecks,
            capabilities.RestartPolicies,
            capabilities.Logs,
            capabilities.ImagePull,
            capabilities.GpuDevices);
    }

    private static ExternalAppPermissionsView ToPermissionsView(ApplicationPermissions permissions)
    {
        return new ExternalAppPermissionsView(permissions.Internet, permissions.LocalNetwork, permissions.HostFiles, permissions.Gpu);
    }

    private static ExternalAppResourcesView ToResourcesView(ApplicationResources resources)
    {
        return new ExternalAppResourcesView(resources.MinimumMemoryMb, resources.RecommendedMemoryMb, resources.CpuHint, resources.PidsLimit);
    }

    private static IReadOnlyList<ExternalAppVariableView> ToVariableViews(IReadOnlyList<ApplicationVariable> variables)
    {
        var views = new List<ExternalAppVariableView>(variables.Count);
        foreach (var variable in variables)
        {
            var isSecret = string.Equals(variable.Type, SecretVariableType, StringComparison.OrdinalIgnoreCase);

            views.Add(new ExternalAppVariableView(variable.Name,
                variable.Label,
                variable.Description,
                variable.Type,
                variable.Required,
                isSecret ? null : variable.Default,
                variable.AllowedValues,
                variable.Advanced,
                variable.Validation is null
                    ? null
                    : new ExternalAppVariableValidationView(variable.Validation.MinLength,
                        variable.Validation.MaxLength,
                        variable.Validation.Pattern)));
        }

        return views;
    }

    /// <summary>
    ///     The observed host bindings, each carrying the manifest's open path for its container port. The url is
    ///     composed only where that path is non-null, so the single Open target is identified by a non-null url rather
    ///     than by "the first published port", which would land the Open button on the wrong application's service.
    /// </summary>
    private static IReadOnlyList<ExternalAppPublishedPortView> ToPublishedPortViews(ApplicationManifest manifest,
        IReadOnlyList<ExternalAppPublishedPort> publishedPorts)
    {
        var openPaths = new Dictionary<(string Service, int ContainerPort), string>();
        foreach (var service in manifest.Services)
        {
            foreach (var port in service.Ports.Where(static port => port.OpenPath is not null))
            {
                openPaths[(service.Name, port.ContainerPort)] = port.OpenPath!;
            }
        }

        var views = new List<ExternalAppPublishedPortView>(publishedPorts.Count);
        foreach (var published in publishedPorts)
        {
            var openPath = openPaths.GetValueOrDefault((published.Service, published.ContainerPort));

            views.Add(new ExternalAppPublishedPortView(published.Service,
                published.ContainerPort,
                published.HostPort,
                openPath,
                openPath is null
                    ? null
                    : string.Create(CultureInfo.InvariantCulture, $"{LoopbackHost}{published.HostPort}{openPath}")));
        }

        return views;
    }

    /// <summary>
    ///     The catalog's <c>generatedAtUtc</c> is authored as an ISO-8601 string; the wire carries unix milliseconds
    ///     like every other time member. An unparseable value reads as 0 rather than failing the whole catalog read:
    ///     the document already passed validation, and a timestamp is not worth a 500.
    /// </summary>
    private static long ToUnixMilliseconds(string generatedAtUtc)
    {
        return DateTimeOffset.TryParse(generatedAtUtc,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : 0L;
    }
}
