namespace XE_Local_AI_Engine.Tests.Endpoints.ExternalApps.V1;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>
///     Wire routes, fixtures and helpers shared by the External Apps endpoint suites.
///     <para>
///         Route literals are SPELLED OUT rather than built from <c>LocalApiRoutes</c>, so a rename surfaces as a
///         failing request here instead of silently moving the contract with the tests. Request bodies are anonymous
///         objects for the same reason: these suites assert what the generated client actually sends.
///     </para>
/// </summary>
internal static class ExternalAppEndpointPayloads
{
    public const string Root = "/api/local/v1/external-apps";

    public const string Runtime = $"{Root}/runtime";

    public const string RuntimeRefresh = $"{Root}/runtime/refresh";

    public const string Catalog = $"{Root}/catalog";

    public const string CatalogRefresh = $"{Root}/catalog/refresh";

    public const string ApplicationId = "odysseus";

    public const string CatalogApplication = $"{Catalog}/{ApplicationId}";

    public const string InstallPreview = $"{CatalogApplication}/install-preview";

    public const string Instances = $"{Root}/instances";

    public const string InstanceIdLiteral = "33333333-3333-3333-3333-333333333333";

    public const string Instance = $"{Instances}/{InstanceIdLiteral}";

    public const string InstanceUpdatePreview = $"{Instance}/update-preview";

    public const string InstanceStart = $"{Instance}/start";

    public const string InstanceStop = $"{Instance}/stop";

    public const string InstanceRestart = $"{Instance}/restart";

    public const string InstanceReset = $"{Instance}/reset";

    public const string InstanceUpdate = $"{Instance}/update";

    public const string InstanceCancel = $"{Instance}/cancel";

    public const string InstanceVariables = $"{Instance}/variables";

    public const string InstanceEvents = $"{Instance}/events";

    public const string InstanceLogs = $"{Instance}/logs";

    /// <summary>The fingerprint every fixture manifest carries; sixty-four lowercase hex characters.</summary>
    public const string ManifestSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>A well-formed fingerprint that is not <see cref="ManifestSha256" />, for the manifest-moved cases.</summary>
    public const string OtherManifestSha256 = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    public const string SecretVariableName = "ODYSSEUS_ADMIN_PASSWORD";

    public const string PlainVariableName = "LLM_HOST";

    public static readonly Guid InstanceId = Guid.Parse(InstanceIdLiteral);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    /// <summary>
    ///     A host with <c>ExternalApps:Enabled=true</c> and every External Apps collaborator substituted. The feature
    ///     flag is on because these suites test the endpoints, not the kill switch, which has its own suite.
    /// </summary>
    public static TestServerWebAppFactory EnabledFactory(IExternalAppService? apps = null,
        IApplicationCatalogProvider? catalog = null,
        IContainerRuntimeResolver? resolver = null,
        IExternalAppStartupReconciler? reconciler = null,
        IExternalAppInstanceStore? store = null) =>
        Factory(enabled: true, apps, catalog, resolver, reconciler, store);

    /// <summary>
    ///     The same host with the shipped default, <c>ExternalApps:Enabled=false</c>. The collaborators are still
    ///     substituted, which is what lets the kill-switch suite assert that a refused request reached NONE of them.
    /// </summary>
    public static TestServerWebAppFactory DisabledFactory(IExternalAppService? apps = null,
        IApplicationCatalogProvider? catalog = null,
        IContainerRuntimeResolver? resolver = null,
        IExternalAppStartupReconciler? reconciler = null,
        IExternalAppInstanceStore? store = null) =>
        Factory(enabled: false, apps, catalog, resolver, reconciler, store);

    private static TestServerWebAppFactory Factory(bool enabled,
        IExternalAppService? apps,
        IApplicationCatalogProvider? catalog,
        IContainerRuntimeResolver? resolver,
        IExternalAppStartupReconciler? reconciler,
        IExternalAppInstanceStore? store) =>
        new()
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ExternalApps:Enabled"] = enabled ? "true" : "false"
            },
            ConfigureAdditionalTestServices = services =>
            {
                Replace(services, apps);
                Replace(services, catalog);
                Replace(services, resolver);
                Replace(services, reconciler);
                Replace(services, store);
            }
        };

    public static async Task<HttpResponseMessage> SendAsOperatorAsync(TestServerWebAppFactory factory,
        string method,
        string route,
        object? body = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        using var client = factory.CreateClient();
        using var request = Request(method, route, body);
        factory.AddNodeBearerToken(request);
        return await client.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> SendAsNonOperatorAsync(TestServerWebAppFactory factory, string method, string route)
    {
        ArgumentNullException.ThrowIfNull(factory);

        using var client = factory.CreateClient();
        using var request = Request(method, route);
        factory.AddNonOperatorBearerToken(request);
        return await client.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> SendAnonymousAsync(TestServerWebAppFactory factory, string method, string route)
    {
        ArgumentNullException.ThrowIfNull(factory);

        using var client = factory.CreateClient();
        using var request = Request(method, route);
        return await client.SendAsync(request);
    }

    public static HttpRequestMessage Request(string method, string route, object? body = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method is "POST" or "PUT" or "PATCH")
        {
            request.Content = new StringContent(body is null ? "{}" : JsonSerializer.Serialize(body, Json),
                Encoding.UTF8,
                "application/json");
        }

        return request;
    }

    /// <summary>Reads the response body as a parsed document, so assertions name the WIRE member rather than a DTO property.</summary>
    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    /// <summary>
    ///     Two services on purpose: <c>web</c> is unprivileged and <c>worker</c> adds a capability, is writable and
    ///     names an extra host, so the per-service permission map cannot pass by repeating one aggregate. Only
    ///     <c>web</c>'s port carries an open path, which is the manifest rule the Open target is selected by.
    /// </summary>
    public static ApplicationManifest Manifest(string id = ApplicationId, int manifestVersion = 2, string testedVersion = "1.4.0") =>
        new(id,
            manifestVersion,
            ManifestSha256,
            "Odysseus",
            "A curated application.",
            "The long description.",
            "https://example.invalid/odysseus",
            "Apache-2.0",
            "verified",
            testedVersion,
            ["containers", "networks", "bindStorage"],
            new ApplicationPermissions(Internet: true, LocalNetwork: false, "none", "none"),
            new ApplicationResources(1024, 2048, 2, 256),
            [
                new ApplicationService("web",
                    "ghcr.io/example/odysseus@sha256:aaaa",
                    "1.4.0",
                    Entrypoint: null,
                    Command: null,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["PORT"] = "8080"
                    },
                    [new ApplicationPort(8080, "ui", 18080, "/app")],
                    [new ApplicationStorage("data", "/var/lib/odysseus")],
                    [new ApplicationFile("seed.json", "/etc/odysseus/seed.json", "abc", "e30=")],
                    new ApplicationHealthcheck(["CMD", "true"], 10, 5, 3, 20),
                    [],
                    [],
                    [],
                    ReadOnlyRootFilesystem: true),
                new ApplicationService("worker",
                    "ghcr.io/example/odysseus-worker@sha256:bbbb",
                    "1.4.0",
                    Entrypoint: null,
                    Command: null,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    [new ApplicationPort(9090, "ui", null, null)],
                    [],
                    [],
                    Healthcheck: null,
                    [new ApplicationDependency("web", "healthy")],
                    ["CHOWN"],
                    ["registry.internal:127.0.0.1"],
                    ReadOnlyRootFilesystem: false)
            ],
            [
                new ApplicationVariable(SecretVariableName,
                    "Admin password",
                    "The initial admin password.",
                    "secret",
                    Required: true,
                    "shipped-default-that-must-never-cross-the-wire",
                    AllowedValues: null,
                    new ApplicationVariableValidation(8, 128, null),
                    Advanced: false),
                new ApplicationVariable(PlainVariableName,
                    "LLM host",
                    Description: null,
                    "string",
                    Required: false,
                    "http://127.0.0.1:11434",
                    AllowedValues: null,
                    Validation: null,
                    Advanced: true)
            ]);

    public static ExternalAppCatalogSnapshot CatalogSnapshot(ExternalAppCatalogSource source = ExternalAppCatalogSource.Bundled,
        string? lastRefreshFailure = null,
        params ApplicationManifest[] applications) =>
        new(new ExternalAppCatalogDocument(1, "2026-09-01T00:00:00Z", applications.Length == 0 ? [Manifest()] : applications),
            source,
            DateTimeOffset.UnixEpoch.AddSeconds(1_780_000_000),
            source == ExternalAppCatalogSource.Bundled ? null : "https://example.invalid/catalog.json",
            lastRefreshFailure);

    public static ContainerRuntimeResolution Resolution(ContainerRuntimeStatus status = ContainerRuntimeStatus.Ready,
        string? daemonId = "daemon-abc",
        string? pinnedDaemonId = "daemon-abc") =>
        new()
        {
            Provider = "docker",
            Status = status,
            Capabilities = status == ContainerRuntimeStatus.Ready ? Capabilities() : ContainerRuntimeCapabilities.None,
            Message = status == ContainerRuntimeStatus.Ready
                ? "Docker is ready."
                : "Docker answered but refused this process; check your Docker socket permissions.",
            Daemon = new ContainerDaemonSummary
            {
                Endpoint = "unix:///var/run/docker.sock",
                EndpointSource = DockerDaemonEndpointSource.DefaultUnixSocket,
                DaemonId = daemonId,
                ServerVersion = daemonId is null ? null : "27.1.1",
                ApiVersion = daemonId is null ? null : "1.46",
                IsRootless = false,
                PinnedDaemonId = pinnedDaemonId,
                PinnedDaemonConfirmedAtUtc = pinnedDaemonId is null ? null : DateTimeOffset.UnixEpoch.AddSeconds(1_779_000_000)
            }
        };

    public static ContainerRuntimeCapabilities Capabilities() =>
        new()
        {
            Containers = true,
            Networks = true,
            BindStorage = true,
            LoopbackPortPublishing = true,
            HealthChecks = true,
            RestartPolicies = true,
            Logs = true,
            ImagePull = true,
            GpuDevices = false
        };

    public static ExternalAppResourceVerdict ResourceVerdict(bool satisfied = true) =>
        new()
        {
            Satisfied = satisfied,
            FailureCategory = satisfied ? null : ExternalAppFailureCategory.InsufficientMemory,
            RequiredMemoryBytes = 1_073_741_824L,
            AvailableMemoryBytes = satisfied ? 8_589_934_592L : 536_870_912L,
            RequiredDiskBytes = 2_147_483_648L,
            AvailableDiskBytes = 17_179_869_184L,
            Message = satisfied ? "Enough memory and disk are free." : "This application needs 1 GB of memory; 512 MB is free."
        };

    public static InstallPreview Preview(ApplicationManifest? manifest = null,
        bool canInstall = true,
        ExternalAppBlockedReason? blockedReason = null,
        ContainerRuntimeResolution? resolution = null,
        Guid? existingInstanceId = null)
    {
        var source = manifest ?? Manifest();

        return new InstallPreview
        {
            ApplicationId = source.Id,
            ManifestVersion = source.ManifestVersion,
            ManifestSha256 = source.ManifestSha256,
            CanInstall = canInstall,
            BlockedReason = blockedReason,
            ExistingInstanceId = existingInstanceId,
            Permissions = source.Permissions,
            EffectivePermissions = ExternalAppEffectivePermissions.From(source),
            Variables = source.Variables,
            Resources = ResourceVerdict(satisfied: canInstall || blockedReason != ExternalAppBlockedReason.InsufficientMemory),
            Runtime = resolution ?? Resolution(),
            MissingCapabilities = []
        };
    }

    public static UpdatePreview UpdatePreviewOf(ApplicationManifest? target = null,
        bool canUpdate = true,
        ExternalAppBlockedReason? blockedReason = null,
        IReadOnlyList<string>? addedPermissions = null,
        IReadOnlyDictionary<string, string>? currentValues = null)
    {
        var manifest = target ?? Manifest(manifestVersion: 3);

        return new UpdatePreview
        {
            ApplicationId = manifest.Id,
            InstanceId = InstanceId,
            CurrentManifestVersion = 2,
            TargetManifestVersion = manifest.ManifestVersion,
            ManifestSha256 = manifest.ManifestSha256,
            Variables = manifest.Variables,
            CurrentValues = currentValues ?? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SecretVariableName] = ExternalAppVariableMask.Value,
                [PlainVariableName] = "http://127.0.0.1:11434"
            },
            AddedPermissions = addedPermissions ?? [],
            EffectivePermissions = ExternalAppEffectivePermissions.From(manifest),
            ResourceVerdict = ResourceVerdict(),
            CanUpdate = canUpdate,
            BlockedReason = blockedReason
        };
    }

    public static ExternalAppInstanceSummary Summary(ExternalAppInstanceStatus status = ExternalAppInstanceStatus.Running,
        long version = 7,
        bool updateAvailable = false,
        bool catalogMissing = false) =>
        new()
        {
            Id = InstanceId,
            ApplicationId = ApplicationId,
            DisplayName = "Odysseus",
            ManifestVersion = 2,
            Status = status,
            DesiredState = status == ExternalAppInstanceStatus.Running ? ExternalAppDesiredState.Running : ExternalAppDesiredState.Stopped,
            FailureCategory = null,
            FailureSummary = null,
            UpdateAvailable = updateAvailable,
            AvailableManifestVersion = updateAvailable ? 3 : null,
            CatalogMissing = catalogMissing,
            UpdatedAtUtc = 1_780_000_000_000L,
            Version = version
        };

    /// <summary>
    ///     One installed instance. The variables arrive ALREADY masked, because masking happens where the stored values
    ///     are read; the endpoint layer never owns a second mask constant.
    /// </summary>
    public static ExternalAppInstanceDetail Detail(ExternalAppInstanceSummary? summary = null, ApplicationManifest? manifest = null) =>
        new()
        {
            Summary = summary ?? Summary(),
            Manifest = manifest ?? Manifest(),
            TestedVersion = "1.4.0",
            MaskedVariables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SecretVariableName] = ExternalAppVariableMask.Value,
                [PlainVariableName] = "http://127.0.0.1:11434"
            },
            PublishedPorts = [new ExternalAppPublishedPort { Service = "web", ContainerPort = 8080, HostPort = 18080 }, new ExternalAppPublishedPort { Service = "worker", ContainerPort = 9090, HostPort = 19090 }],
            RuntimeProvider = "docker",
            RuntimeOverride = null,
            StoragePath = "/var/lib/xe/external-apps/odysseus",
            LastSequence = 42,
            NeedsRecreate = false,
            InstalledAtUtc = 1_779_000_000_000L,
            StartedAtUtc = 1_779_500_000_000L,
            StoppedAtUtc = null
        };

    /// <summary>A body for the install POST. Anonymous so a DTO rename shows up as a failing request.</summary>
    public static object InstallBody(string? applicationId = ApplicationId,
        int manifestVersion = 2,
        string manifestSha256 = ManifestSha256,
        bool acceptPermissions = true,
        IReadOnlyDictionary<string, string>? variables = null,
        string? displayName = null) =>
        new
        {
            applicationId,
            displayName,
            manifestVersion,
            manifestSha256,
            acceptPermissions,
            variables = variables ?? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SecretVariableName] = "a-real-password",
                [PlainVariableName] = "http://127.0.0.1:11434"
            }
        };

    private static void Replace<T>(IServiceCollection services, T? substitute)
        where T : class
    {
        if (substitute is null)
        {
            return;
        }

        services.RemoveAll<T>();
        services.AddSingleton(substitute);
    }
}
