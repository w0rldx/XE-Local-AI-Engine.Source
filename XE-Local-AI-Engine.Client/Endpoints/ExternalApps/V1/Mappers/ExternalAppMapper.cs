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

        return new ExternalAppRuntimeResponse
        {
            Provider = resolution.Provider,
            Status = resolution.Status.ToString(),
            Available = resolution.Available,
            Ready = resolution.Ready,
            Message = resolution.Message,
            RequiresOperatorConfirmation = resolution.RequiresOperatorConfirmation,
            Endpoint = daemon.Endpoint,
            EndpointSource = daemon.EndpointSource.ToString(),
            ObservedDaemon = daemon.DaemonId is null
                ? null
                : new ExternalAppDaemonView { DaemonId = daemon.DaemonId, ServerVersion = daemon.ServerVersion, Endpoint = daemon.Endpoint, ConfirmedAtUtc = null },
            PinnedDaemon = daemon.PinnedDaemonId is null
                ? null
                : new ExternalAppDaemonView
                {
                    DaemonId = daemon.PinnedDaemonId,
                    ServerVersion = null,
                    Endpoint = null,
                    ConfirmedAtUtc = daemon.PinnedDaemonConfirmedAtUtc?.ToUnixTimeMilliseconds()
                },
            Capabilities = ToCapabilitiesView(resolution.Capabilities),
            ForeignInstallContainers = foreignInstallContainers
        };
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

        return new ExternalAppCatalogResponse
        {
            SchemaVersion = snapshot.Document.SchemaVersion,
            GeneratedAtUtc = ToUnixMilliseconds(snapshot.Document.GeneratedAtUtc),
            FetchedAtUtc = snapshot.FetchedAtUtc?.ToUnixTimeMilliseconds() ?? 0L,
            FromBundledSeed = snapshot.Source == ExternalAppCatalogSource.Bundled,
            RefreshFailureMessage = refreshFailureMessage,
            LastRefreshFailure = snapshot.LastRefreshFailure,
            Applications = applications
        };
    }

    /// <summary>One catalog card, carrying the instance join when this application already has one.</summary>
    public static ExternalAppSummaryView ToSummaryView(ApplicationManifest manifest,
        IReadOnlyDictionary<string, ExternalAppInstanceSummary> installedByApplicationId)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(installedByApplicationId);

        _ = installedByApplicationId.TryGetValue(manifest.Id, out var installed);

        return new ExternalAppSummaryView
        {
            Id = manifest.Id,
            ManifestVersion = manifest.ManifestVersion,
            DisplayName = manifest.DisplayName,
            Summary = manifest.Summary,
            Homepage = manifest.Homepage,
            License = manifest.License,
            Trust = manifest.Trust,
            TestedVersion = manifest.TestedVersion,
            Requires = manifest.Requires,
            Permissions = ToPermissionsView(manifest.Permissions),
            Resources = ToResourcesView(manifest.Resources),
            InstalledInstanceId = installed?.Id,
            InstalledStatus = installed?.Status.ToString()
        };
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
                ports.Add(new ExternalAppPortView { ContainerPort = port.ContainerPort, Role = port.Role, PreferredHostPort = port.PreferredHostPort, OpenPath = port.OpenPath });
            }

            var storage = new List<ExternalAppStorageView>(service.Storage.Count);
            foreach (var entry in service.Storage)
            {
                storage.Add(new ExternalAppStorageView { Name = entry.Name, ContainerPath = entry.ContainerPath });
            }

            var dependsOn = new List<ExternalAppDependencyView>(service.DependsOn.Count);
            foreach (var dependency in service.DependsOn)
            {
                dependsOn.Add(new ExternalAppDependencyView { Service = dependency.Service, Condition = dependency.Condition });
            }

            services.Add(new ExternalAppServiceView
            {
                Name = service.Name,
                Image = service.Image,
                ImageTag = service.ImageTag,
                Entrypoint = service.Entrypoint,
                Command = service.Command,
                Environment = service.Environment,
                Ports = ports,
                Storage = storage,
                HasHealthcheck = service.Healthcheck is not null,
                DependsOn = dependsOn,
                CapAdd = service.CapAdd,
                ExtraHosts = service.ExtraHosts,
                ReadOnlyRootFilesystem = service.ReadOnlyRootFilesystem
            });
        }

        return new ExternalAppManifestView
        {
            Id = manifest.Id,
            ManifestVersion = manifest.ManifestVersion,
            DisplayName = manifest.DisplayName,
            Summary = manifest.Summary,
            Description = manifest.Description,
            Homepage = manifest.Homepage,
            License = manifest.License,
            Trust = manifest.Trust,
            TestedVersion = manifest.TestedVersion,
            Requires = manifest.Requires,
            Permissions = ToPermissionsView(manifest.Permissions),
            Resources = ToResourcesView(manifest.Resources),
            Services = services,
            Variables = ToVariableViews(manifest.Variables)
        };
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

        return new ExternalAppInstallPreview
        {
            ApplicationId = preview.ApplicationId,
            ManifestVersion = preview.ManifestVersion,
            ManifestSha256 = preview.ManifestSha256,
            CanInstall = preview.CanInstall,
            BlockedReason = preview.BlockedReason?.ToString(),
            ExistingInstanceId = preview.ExistingInstanceId,
            Permissions = ToPermissionsView(preview.Permissions),
            EffectivePermissions = ToEffectivePermissionsView(preview.EffectivePermissions),
            Variables = ToVariableViews(preview.Variables),
            Runtime = runtime,
            MissingCapabilities = preview.MissingCapabilities,
            ResourceCheck = ToResourceCheckView(preview.Resources)
        };
    }

    /// <summary>
    ///     The update disclosure — a member-for-member projection, so a rename on the service side is a compile error
    ///     here rather than a silently null wire field. A catalog-missing preview needs no branch: the service already
    ///     filled <c>CanUpdate: false</c> and the reason.
    /// </summary>
    public static ExternalAppUpdatePreview ToUpdatePreview(UpdatePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        return new ExternalAppUpdatePreview
        {
            ApplicationId = preview.ApplicationId,
            InstanceId = preview.InstanceId,
            CurrentManifestVersion = preview.CurrentManifestVersion,
            TargetManifestVersion = preview.TargetManifestVersion,
            ManifestSha256 = preview.ManifestSha256,
            Variables = ToVariableViews(preview.Variables),
            CurrentValues = preview.CurrentValues,
            AddedPermissions = preview.AddedPermissions,
            EffectivePermissions = ToEffectivePermissionsView(preview.EffectivePermissions),
            ResourceVerdict = ToResourceCheckView(preview.ResourceVerdict),
            CanUpdate = preview.CanUpdate,
            BlockedReason = preview.BlockedReason?.ToString()
        };
    }

    /// <summary>
    ///     One installed instance in full. The variables are the service's already-masked map — the masking happens
    ///     where the stored values are read, so there is one place that decides and no second mask constant here.
    /// </summary>
    public static ExternalAppInstanceView ToView(ExternalAppInstanceDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var summary = detail.Summary;

        return new ExternalAppInstanceView
        {
            Id = summary.Id,
            ApplicationId = summary.ApplicationId,
            DisplayName = summary.DisplayName,
            ManifestVersion = summary.ManifestVersion,
            Status = summary.Status.ToString(),
            DesiredState = summary.DesiredState.ToString(),
            RuntimeOverride = detail.RuntimeOverride,
            RuntimeProvider = detail.RuntimeProvider,
            Manifest = ToManifestView(detail.Manifest),
            PublishedPorts = ToPublishedPortViews(detail.Manifest, detail.PublishedPorts),
            Variables = detail.MaskedVariables,
            FailureCategory = summary.FailureCategory?.ToString(),
            FailureSummary = summary.FailureSummary,
            UpdateAvailable = summary.UpdateAvailable,
            AvailableManifestVersion = summary.AvailableManifestVersion,
            CatalogMissing = summary.CatalogMissing,
            InstalledAtUtc = detail.InstalledAtUtc,
            StartedAtUtc = detail.StartedAtUtc,
            StoppedAtUtc = detail.StoppedAtUtc,
            UpdatedAtUtc = summary.UpdatedAtUtc,
            LastSequence = detail.LastSequence,
            Version = summary.Version
        };
    }

    /// <summary>The admitted row a lifecycle command answers with. The instance LIST renders full views instead.</summary>
    public static ExternalAppInstanceSummaryView ToSummaryView(ExternalAppInstanceSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new ExternalAppInstanceSummaryView
        {
            Id = summary.Id,
            ApplicationId = summary.ApplicationId,
            DisplayName = summary.DisplayName,
            ManifestVersion = summary.ManifestVersion,
            Status = summary.Status.ToString(),
            DesiredState = summary.DesiredState.ToString(),
            FailureCategory = summary.FailureCategory?.ToString(),
            FailureSummary = summary.FailureSummary,
            UpdateAvailable = summary.UpdateAvailable,
            AvailableManifestVersion = summary.AvailableManifestVersion,
            CatalogMissing = summary.CatalogMissing,
            UpdatedAtUtc = summary.UpdatedAtUtc,
            Version = summary.Version
        };
    }

    /// <summary>One row of the append-only event feed, shared by the paged read and the hub replay.</summary>
    public static ExternalAppInstanceEventView ToEventView(ExternalAppInstanceEventSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new ExternalAppInstanceEventView { Sequence = snapshot.Sequence, AtUtc = snapshot.OccurredAtUtc, Kind = snapshot.Kind.ToString(), DetailJson = snapshot.DetailJson };
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
            services[entry.Key] = new ExternalAppServicePermissionsView
            {
                Capabilities = [.. entry.Value.Capabilities],
                WritableRootFilesystem = entry.Value.WritableRootFilesystem,
                PublishedPorts = [.. entry.Value.PublishedPorts],
                ExtraHosts = [.. entry.Value.ExtraHosts]
            };

            capabilities.UnionWith(entry.Value.Capabilities);
            publishedPorts.UnionWith(entry.Value.PublishedPorts);
            extraHosts.UnionWith(entry.Value.ExtraHosts);
            writableRootFilesystem |= entry.Value.WritableRootFilesystem;
        }

        return new ExternalAppEffectivePermissionsView
        {
            Internet = permissions.Internet,
            HostFiles = permissions.HostFiles,
            Gpu = permissions.Gpu,
            LocalNetwork = permissions.LocalNetwork,
            Services = services,
            Capabilities = [.. capabilities],
            WritableRootFilesystem = writableRootFilesystem,
            PublishedPorts = [.. publishedPorts],
            ExtraHosts = [.. extraHosts]
        };
    }

    private static ExternalAppResourceCheckView ToResourceCheckView(ExternalAppResourceVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return new ExternalAppResourceCheckView
        {
            Satisfied = verdict.Satisfied,
            FailureCategory = verdict.FailureCategory?.ToString(),
            RequiredMemoryBytes = verdict.RequiredMemoryBytes,
            AvailableMemoryBytes = verdict.AvailableMemoryBytes,
            RequiredDiskBytes = verdict.RequiredDiskBytes,
            AvailableDiskBytes = verdict.AvailableDiskBytes,
            Message = verdict.Message
        };
    }

    private static ExternalAppCapabilitiesView ToCapabilitiesView(ContainerRuntimeCapabilities capabilities)
    {
        return new ExternalAppCapabilitiesView
        {
            Containers = capabilities.Containers,
            Networks = capabilities.Networks,
            BindStorage = capabilities.BindStorage,
            LoopbackPortPublishing = capabilities.LoopbackPortPublishing,
            HealthChecks = capabilities.HealthChecks,
            RestartPolicies = capabilities.RestartPolicies,
            Logs = capabilities.Logs,
            ImagePull = capabilities.ImagePull,
            GpuDevices = capabilities.GpuDevices
        };
    }

    private static ExternalAppPermissionsView ToPermissionsView(ApplicationPermissions permissions)
    {
        return new ExternalAppPermissionsView { Internet = permissions.Internet, LocalNetwork = permissions.LocalNetwork, HostFiles = permissions.HostFiles, Gpu = permissions.Gpu };
    }

    private static ExternalAppResourcesView ToResourcesView(ApplicationResources resources)
    {
        return new ExternalAppResourcesView { MinimumMemoryMb = resources.MinimumMemoryMb, RecommendedMemoryMb = resources.RecommendedMemoryMb, CpuHint = resources.CpuHint, PidsLimit = resources.PidsLimit };
    }

    private static IReadOnlyList<ExternalAppVariableView> ToVariableViews(IReadOnlyList<ApplicationVariable> variables)
    {
        var views = new List<ExternalAppVariableView>(variables.Count);
        foreach (var variable in variables)
        {
            var isSecret = string.Equals(variable.Type, SecretVariableType, StringComparison.OrdinalIgnoreCase);

            views.Add(new ExternalAppVariableView
            {
                Name = variable.Name,
                Label = variable.Label,
                Description = variable.Description,
                Type = variable.Type,
                Required = variable.Required,
                Default = isSecret ? null : variable.Default,
                AllowedValues = variable.AllowedValues,
                Advanced = variable.Advanced,
                Validation = variable.Validation is null
                    ? null
                    : new ExternalAppVariableValidationView
                    {
                        MinLength = variable.Validation.MinLength,
                        MaxLength = variable.Validation.MaxLength,
                        Pattern = variable.Validation.Pattern
                    }
            });
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

            views.Add(new ExternalAppPublishedPortView
            {
                Service = published.Service,
                ContainerPort = published.ContainerPort,
                HostPort = published.HostPort,
                OpenPath = openPath,
                Url = openPath is null
                    ? null
                    : string.Create(CultureInfo.InvariantCulture, $"{LoopbackHost}{published.HostPort}{openPath}")
            });
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
