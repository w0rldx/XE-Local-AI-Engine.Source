namespace XE_Local_AI_Engine.Tests.Endpoints.ExternalApps.V1;

using System.Net;
using System.Text.Json;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Tests.Containers;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The read half of the External Apps surface: the runtime panel, the catalog and the install preview. Everything
///     here is operator-gated and everything here is a projection, so the assertions are about what crosses the wire —
///     never an asset body, never a secret, and never a verdict this layer re-derived.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ExternalAppCatalogEndpointTests
{
    [Test]
    [Arguments("GET", ExternalAppEndpointPayloads.Runtime)]
    [Arguments("POST", ExternalAppEndpointPayloads.RuntimeRefresh)]
    [Arguments("GET", ExternalAppEndpointPayloads.Catalog)]
    [Arguments("POST", ExternalAppEndpointPayloads.CatalogRefresh)]
    [Arguments("GET", ExternalAppEndpointPayloads.CatalogApplication)]
    [Arguments("GET", ExternalAppEndpointPayloads.InstallPreview)]
    public async Task Route_WithoutAToken_ReturnsUnauthorized(string method, string route)
    {
        await using var factory = Factory();

        using var response = await ExternalAppEndpointPayloads.SendAnonymousAsync(factory, method, route);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {route} must require a token.");
    }

    [Test]
    [Arguments("GET", ExternalAppEndpointPayloads.Runtime)]
    [Arguments("POST", ExternalAppEndpointPayloads.RuntimeRefresh)]
    [Arguments("GET", ExternalAppEndpointPayloads.Catalog)]
    [Arguments("POST", ExternalAppEndpointPayloads.CatalogRefresh)]
    [Arguments("GET", ExternalAppEndpointPayloads.CatalogApplication)]
    [Arguments("GET", ExternalAppEndpointPayloads.InstallPreview)]
    public async Task Route_WithANonOperatorToken_ReturnsForbidden(string method, string route)
    {
        await using var factory = Factory();

        using var response = await ExternalAppEndpointPayloads.SendAsNonOperatorAsync(factory, method, route);

        AssertEx.Equal(HttpStatusCode.Forbidden,
            response.StatusCode,
            $"{method} {route} is operator-only: installing an application container is an administrative act.");
    }

    /// <summary>
    ///     The card carries the summary fields and the install join. It must not carry a manifest: the catalog document
    ///     inlines every asset body, and a list that projected the manifest would ship them to the browser.
    /// </summary>
    [Test]
    public async Task ListCatalog_ProjectsSummariesWithTheInstallJoinAndNoAssetBodies()
    {
        var catalog = Substitute.For<IApplicationCatalogProvider>();
        catalog.GetCatalogAsync(Arg.Any<CancellationToken>()).Returns(ExternalAppEndpointPayloads.CatalogSnapshot());

        var apps = Substitute.For<IExternalAppService>();
        apps.ListAsync(Arg.Any<CancellationToken>()).Returns([ExternalAppEndpointPayloads.Summary()]);

        await using var factory = Factory(apps: apps, catalog: catalog);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.Catalog);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        AssertEx.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        AssertEx.True(root.GetProperty("fromBundledSeed").GetBoolean(), "the fixture serves the shipped seed.");

        var application = root.GetProperty("applications")[0];
        AssertEx.Equal(ExternalAppEndpointPayloads.ApplicationId, application.GetProperty("id").GetString());
        AssertEx.Equal("1.4.0", application.GetProperty("testedVersion").GetString());
        AssertEx.Equal(ExternalAppEndpointPayloads.InstanceIdLiteral, application.GetProperty("installedInstanceId").GetString());
        AssertEx.Equal("Running", application.GetProperty("installedStatus").GetString());
        AssertEx.False(application.TryGetProperty("services", out _), "a card carries no services, so it carries no inlined asset bodies.");
        AssertEx.False(body.Contains("contentBase64", StringComparison.Ordinal), "no asset body reaches the browser.");
    }

    /// <summary>A card for an application nothing has installed leaves both join members null, which is what offers Install.</summary>
    [Test]
    public async Task ListCatalog_ForAnApplicationWithNoInstance_LeavesTheJoinMembersNull()
    {
        var catalog = Substitute.For<IApplicationCatalogProvider>();
        catalog.GetCatalogAsync(Arg.Any<CancellationToken>()).Returns(ExternalAppEndpointPayloads.CatalogSnapshot());

        var apps = Substitute.For<IExternalAppService>();
        apps.ListAsync(Arg.Any<CancellationToken>()).Returns([]);

        await using var factory = Factory(apps: apps, catalog: catalog);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.Catalog);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);

        var application = document.RootElement.GetProperty("applications")[0];
        AssertEx.Equal(JsonValueKind.Null, application.GetProperty("installedInstanceId").ValueKind);
        AssertEx.Equal(JsonValueKind.Null, application.GetProperty("installedStatus").ValueKind);
    }

    /// <summary>
    ///     The manifest read strips the asset bodies and nulls a secret's declared default, and carries no user or uid
    ///     member at either level: containers run as the image's default user and the boundary is the container.
    /// </summary>
    [Test]
    public async Task GetApplication_StripsAssetBodiesAndTheSecretDefault()
    {
        var catalog = Substitute.For<IApplicationCatalogProvider>();
        catalog.GetApplicationAsync(ExternalAppEndpointPayloads.ApplicationId, Arg.Any<CancellationToken>())
               .Returns(ExternalAppEndpointPayloads.Manifest());

        await using var factory = Factory(catalog: catalog);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.CatalogApplication);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        AssertEx.False(root.TryGetProperty("user", out _), "there is no user member on an application.");
        var web = root.GetProperty("services")[0];
        AssertEx.Equal("web", web.GetProperty("name").GetString());
        AssertEx.False(web.TryGetProperty("user", out _), "there is no user member on a service either.");
        AssertEx.False(web.TryGetProperty("files", out _), "files[] are base64 asset bodies the engine materialises, never UI data.");
        AssertEx.True(web.GetProperty("hasHealthcheck").GetBoolean(), "the web service declares a healthcheck.");

        var secret = root.GetProperty("variables")[0];
        AssertEx.Equal(ExternalAppEndpointPayloads.SecretVariableName, secret.GetProperty("name").GetString());
        AssertEx.Equal(JsonValueKind.Null, secret.GetProperty("default").ValueKind, "a shipped secret default is never a wire value.");
        AssertEx.False(body.Contains("shipped-default-that-must-never-cross-the-wire", StringComparison.Ordinal),
            "a shipped secret default never crosses the wire, whatever the manifest says.");

        var plain = root.GetProperty("variables")[1];
        AssertEx.Equal("http://127.0.0.1:11434", plain.GetProperty("default").GetString(), "a non-secret default still crosses.");
    }

    [Test]
    public async Task GetApplication_WhenTheCatalogHasNoSuchApplication_ReturnsNotFound()
    {
        var catalog = Substitute.For<IApplicationCatalogProvider>();
        catalog.GetApplicationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ApplicationManifest?)null);

        await using var factory = Factory(catalog: catalog);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.CatalogApplication);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    ///     Everything the install dialog needs before it asks for anything, in one read. Every declared variable rather
    ///     than the required ones only: <c>required</c> is already a member, and required-only would silently drop an
    ///     application's optional settings at install.
    /// </summary>
    [Test]
    public async Task GetInstallPreview_CarriesTheFingerprintEveryVariableAndTheServerSideVerdicts()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.PreviewInstallAsync(ExternalAppEndpointPayloads.ApplicationId, Arg.Any<CancellationToken>())
            .Returns(ExternalAppEndpointPayloads.Preview());

        await using var factory = Factory(apps: apps);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.InstallPreview);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        AssertEx.Equal(ExternalAppEndpointPayloads.ManifestSha256, root.GetProperty("manifestSha256").GetString());
        AssertEx.Equal(2, root.GetProperty("manifestVersion").GetInt32());
        AssertEx.True(root.GetProperty("canInstall").GetBoolean());
        AssertEx.Equal(JsonValueKind.Null, root.GetProperty("blockedReason").ValueKind, "a reason is set only when the install is blocked.");
        AssertEx.Equal(2, root.GetProperty("variables").GetArrayLength(), "every DECLARED variable, not the required ones only.");
        AssertEx.Equal(0, root.GetProperty("missingCapabilities").GetArrayLength());
        AssertEx.Equal("Enough memory and disk are free.", root.GetProperty("resourceCheck").GetProperty("message").GetString());
        AssertEx.True(root.GetProperty("resourceCheck").GetProperty("satisfied").GetBoolean());
        AssertEx.Equal(0,
            root.GetProperty("runtime").GetProperty("foreignInstallContainers").GetInt32(),
            "nothing reconciles on a preview, so the count is 0 rather than a cached observation.");
        AssertEx.False(body.Contains("shipped-default-that-must-never-cross-the-wire", StringComparison.Ordinal),
            "a shipped secret default never crosses the wire, whatever the manifest says.");
    }

    /// <summary>
    ///     The per-service grant map, which is the whole point of carrying one: the two services differ, so a map that
    ///     repeated the aggregate would fail here. An aggregate alone hides a capability MOVED between services, which
    ///     the server counts as a widening.
    /// </summary>
    [Test]
    public async Task GetInstallPreview_CarriesEffectivePermissionsPerService()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.PreviewInstallAsync(ExternalAppEndpointPayloads.ApplicationId, Arg.Any<CancellationToken>())
            .Returns(ExternalAppEndpointPayloads.Preview());

        await using var factory = Factory(apps: apps);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.InstallPreview);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);

        var effective = document.RootElement.GetProperty("effectivePermissions");
        AssertEx.True(effective.GetProperty("internet").GetBoolean());
        AssertEx.False(effective.GetProperty("localNetwork").GetBoolean());
        AssertEx.Equal("none", effective.GetProperty("hostFiles").GetString());

        var services = effective.GetProperty("services");
        var web = services.GetProperty("web");
        var worker = services.GetProperty("worker");

        AssertEx.Equal(0, web.GetProperty("capabilities").GetArrayLength(), "the web service adds no capability.");
        AssertEx.False(web.GetProperty("writableRootFilesystem").GetBoolean(), "the web service runs read-only.");
        AssertEx.Equal(0, web.GetProperty("extraHosts").GetArrayLength());

        AssertEx.Equal("CHOWN", worker.GetProperty("capabilities")[0].GetString(), "only the worker is privileged.");
        AssertEx.True(worker.GetProperty("writableRootFilesystem").GetBoolean());
        AssertEx.Equal("registry.internal:127.0.0.1", worker.GetProperty("extraHosts")[0].GetString());
        AssertEx.Equal("worker:9090", worker.GetProperty("publishedPorts")[0].GetString());
    }

    /// <summary>
    ///     The four application-level aggregates the panel renders the widening vocabulary from. They are unions of the
    ///     per-service map — one privileged service makes the application privileged — and the server projects them so
    ///     the SPA cannot derive a set the server never computed. Ordinal-sorted: a card must not reshuffle per read.
    /// </summary>
    [Test]
    public async Task GetInstallPreview_CarriesTheApplicationLevelAggregates()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.PreviewInstallAsync(ExternalAppEndpointPayloads.ApplicationId, Arg.Any<CancellationToken>())
            .Returns(ExternalAppEndpointPayloads.Preview());

        await using var factory = Factory(apps: apps);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.InstallPreview);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);

        var effective = document.RootElement.GetProperty("effectivePermissions");

        var capabilities = effective.GetProperty("capabilities");
        AssertEx.Equal(1, capabilities.GetArrayLength(), "the union carries each capability once, however many services add it.");
        AssertEx.Equal("CHOWN", capabilities[0].GetString());

        AssertEx.True(effective.GetProperty("writableRootFilesystem").GetBoolean(),
            "one writable service makes the application's aggregate writable.");

        var publishedPorts = effective.GetProperty("publishedPorts");
        AssertEx.Equal(2, publishedPorts.GetArrayLength());
        AssertEx.Equal("web:8080", publishedPorts[0].GetString(), "ordinal-sorted, so the order is stable across reads.");
        AssertEx.Equal("worker:9090", publishedPorts[1].GetString());

        var extraHosts = effective.GetProperty("extraHosts");
        AssertEx.Equal(1, extraHosts.GetArrayLength());
        AssertEx.Equal("registry.internal:127.0.0.1", extraHosts[0].GetString());
    }

    /// <summary>
    ///     An empty service map aggregates to empty arrays and a false flag, never to a missing member: the panel binds
    ///     eight names unconditionally, and an absent array would render as "unknown" rather than "grants nothing".
    /// </summary>
    [Test]
    public async Task GetInstallPreview_WithNoServices_CarriesEmptyAggregates()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.PreviewInstallAsync(ExternalAppEndpointPayloads.ApplicationId, Arg.Any<CancellationToken>())
            .Returns(ExternalAppEndpointPayloads.Preview(ExternalAppEndpointPayloads.Manifest() with
            {
                Services = []
            }));

        await using var factory = Factory(apps: apps);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.InstallPreview);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);

        var effective = document.RootElement.GetProperty("effectivePermissions");

        AssertEx.Equal(0, effective.GetProperty("services").EnumerateObject().Count());
        AssertEx.Equal(0, effective.GetProperty("capabilities").GetArrayLength());
        AssertEx.False(effective.GetProperty("writableRootFilesystem").GetBoolean(), "no service, nothing writable.");
        AssertEx.Equal(0, effective.GetProperty("publishedPorts").GetArrayLength());
        AssertEx.Equal(0, effective.GetProperty("extraHosts").GetArrayLength());
        AssertEx.True(effective.GetProperty("internet").GetBoolean(), "the application-level flags are unaffected.");
    }

    /// <summary>
    ///     All eight <c>ExternalAppBlockedReason</c> names cross as written, so the SPA ships eight labels once rather
    ///     than two overlapping sets for install and update.
    /// </summary>
    [Test]
    [Arguments(ExternalAppBlockedReason.GpuNotSupported, "GpuNotSupported")]
    [Arguments(ExternalAppBlockedReason.RuntimeIncompatible, "RuntimeIncompatible")]
    [Arguments(ExternalAppBlockedReason.RuntimeUnavailable, "RuntimeUnavailable")]
    [Arguments(ExternalAppBlockedReason.InsufficientMemory, "InsufficientMemory")]
    [Arguments(ExternalAppBlockedReason.InsufficientDisk, "InsufficientDisk")]
    [Arguments(ExternalAppBlockedReason.AlreadyInstalled, "AlreadyInstalled")]
    [Arguments(ExternalAppBlockedReason.CatalogMissing, "CatalogMissing")]
    [Arguments(ExternalAppBlockedReason.BridgeUnavailable, "BridgeUnavailable")]
    public async Task GetInstallPreview_WhenBlocked_CarriesTheReasonName(ExternalAppBlockedReason reason, string expected)
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.PreviewInstallAsync(ExternalAppEndpointPayloads.ApplicationId, Arg.Any<CancellationToken>())
            .Returns(ExternalAppEndpointPayloads.Preview(canInstall: false, blockedReason: reason));

        await using var factory = Factory(apps: apps);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.InstallPreview);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);

        AssertEx.False(document.RootElement.GetProperty("canInstall").GetBoolean());
        AssertEx.Equal(expected, document.RootElement.GetProperty("blockedReason").GetString());
    }

    /// <summary>An unknown application id reaches the not-found handler rather than the catch-all, so it is a 404 and not a 500.</summary>
    [Test]
    public async Task GetInstallPreview_ForAnUnknownApplication_ReturnsNotFound()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.PreviewInstallAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<InstallPreview>(_ => throw new ExternalAppNotFoundException("No application 'odysseus' is in the catalog."));

        await using var factory = Factory(apps: apps);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.InstallPreview);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task GetRuntime_ProjectsTheResolutionWithoutPinningADaemon()
    {
        var resolver = Substitute.For<IContainerRuntimeResolver>();
        resolver.ResolveAsync(Arg.Any<ContainerRuntimeSelection?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(ExternalAppEndpointPayloads.Resolution());

        await using var factory = Factory(resolver: resolver);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.Runtime);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);
        var root = document.RootElement;

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("docker", root.GetProperty("provider").GetString());
        AssertEx.Equal("Ready", root.GetProperty("status").GetString());
        AssertEx.True(root.GetProperty("available").GetBoolean());
        AssertEx.True(root.GetProperty("ready").GetBoolean());
        AssertEx.Equal("Docker is ready.", root.GetProperty("message").GetString());
        AssertEx.False(root.GetProperty("requiresOperatorConfirmation").GetBoolean());
        AssertEx.Equal("daemon-abc", root.GetProperty("observedDaemon").GetProperty("daemonId").GetString());
        AssertEx.Equal("daemon-abc", root.GetProperty("pinnedDaemon").GetProperty("daemonId").GetString());
        AssertEx.Equal(JsonValueKind.Null,
            root.GetProperty("pinnedDaemon").GetProperty("serverVersion").ValueKind,
            "the pin records who was approved, not what version answered.");
        AssertEx.Equal(0, root.GetProperty("foreignInstallContainers").GetInt32(), "a GET reconciles nothing.");
        AssertEx.True(root.GetProperty("capabilities").GetProperty("containers").GetBoolean());
        AssertEx.False(root.GetProperty("capabilities").GetProperty("gpuDevices").GetBoolean());

        await resolver.DidNotReceive().ConfirmDaemonIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The runtime card renders BOTH the resolution's message and its endpoint, so it is the widest rendering of a
    ///     refused endpoint on the whole surface. The resolution comes from the real resolver rather than from a
    ///     fixture: a hand-written one would pass whatever the redaction did, which is the thing under test.
    /// </summary>
    [Test]
    public async Task GetRuntime_WhenTheEndpointHidesASecret_RendersNeitherHalfOfItAnywhereInTheBody()
    {
        var resolution = await DisclosingEndpointRefusal.ResolveAsync();
        var resolver = Substitute.For<IContainerRuntimeResolver>();
        resolver.ResolveAsync(Arg.Any<ContainerRuntimeSelection?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(resolution);

        await using var factory = Factory(resolver: resolver);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.Runtime);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(body.Contains(DisclosingEndpointRefusal.QuerySentinel, StringComparison.Ordinal),
            $"The runtime body carries the query string's value: {body}");
        AssertEx.False(body.Contains(DisclosingEndpointRefusal.FragmentSentinel, StringComparison.Ordinal),
            $"The runtime body carries the fragment's value: {body}");
        // Still useful to the operator: the host is named, so they know which endpoint to go and fix.
        AssertEx.Contains(body, "docker.remote:2375", StringComparison.Ordinal);
        AssertEx.Contains(body, "a query string", StringComparison.Ordinal);
    }

    /// <summary>
    ///     A probe that reached nothing reports a NULL <c>observedDaemon</c> rather than a view of null members, and
    ///     <c>available: false</c> — the one member the SPA renders "Runtime unavailable" from.
    /// </summary>
    [Test]
    public async Task GetRuntime_WhenNothingAnswered_ReportsUnavailableWithANullDaemonId()
    {
        var resolver = Substitute.For<IContainerRuntimeResolver>();
        resolver.ResolveAsync(Arg.Any<ContainerRuntimeSelection?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(ExternalAppEndpointPayloads.Resolution(ContainerRuntimeStatus.DaemonUnreachable, daemonId: null, pinnedDaemonId: null));

        await using var factory = Factory(resolver: resolver);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.Runtime);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);
        var root = document.RootElement;

        AssertEx.False(root.GetProperty("available").GetBoolean());
        AssertEx.False(root.GetProperty("ready").GetBoolean());
        AssertEx.Equal(JsonValueKind.Null,
            root.GetProperty("observedDaemon").ValueKind,
            "no daemon answered, so the whole view is null rather than an object whose id happens to be null.");
        AssertEx.Equal(JsonValueKind.Null, root.GetProperty("pinnedDaemon").ValueKind, "this node has never pinned a daemon.");
    }

    /// <summary>
    ///     The case that proves the mapper READS <c>Available</c> instead of re-deriving it from the status. A daemon
    ///     that answered and refused this process is available but not ready, and the operator's sentence is "check
    ///     your Docker socket permissions", not "no runtime found".
    /// </summary>
    [Test]
    public async Task GetRuntime_WhenPermissionIsDenied_IsAvailableButNotReady()
    {
        var resolver = Substitute.For<IContainerRuntimeResolver>();
        resolver.ResolveAsync(Arg.Any<ContainerRuntimeSelection?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(ExternalAppEndpointPayloads.Resolution(ContainerRuntimeStatus.PermissionDenied));

        await using var factory = Factory(resolver: resolver);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.Runtime);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);

        AssertEx.True(document.RootElement.GetProperty("available").GetBoolean(), "the daemon answered this probe.");
        AssertEx.False(document.RootElement.GetProperty("ready").GetBoolean(), "and the node still cannot use it.");
        AssertEx.Equal("PermissionDenied", document.RootElement.GetProperty("status").GetString());
    }

    /// <summary>An omitted acknowledgement re-probes and approves nothing. That is the default, and it must stay one.</summary>
    [Test]
    public async Task RefreshRuntime_WithoutADaemonId_ReProbesWithoutConfirming()
    {
        var resolver = Substitute.For<IContainerRuntimeResolver>();
        resolver.ResolveAsync(Arg.Any<ContainerRuntimeSelection?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(ExternalAppEndpointPayloads.Resolution());

        var reconciler = Substitute.For<IExternalAppStartupReconciler>();
        reconciler.ReconcileAsync(Arg.Any<CancellationToken>()).Returns(new ExternalAppReconcileSummary
        {
            RowsInspected = 3,
            RowsChanged = 1,
            OrphansRemoved = 0,
            ForeignInstallContainers = 2,
            RowsSkippedBusy = 1
        });

        await using var factory = Factory(resolver: resolver, reconciler: reconciler);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory, "POST", ExternalAppEndpointPayloads.RuntimeRefresh);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(2,
            document.RootElement.GetProperty("foreignInstallContainers").GetInt32(),
            "a ready preflight reconciles, and the count is the reconciler's own observation.");
        await resolver.DidNotReceive().ConfirmDaemonIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await resolver.Received(1)
                      .ResolveAsync(Arg.Any<ContainerRuntimeSelection?>(), forceRefresh: true, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RefreshRuntime_WithTheObservedDaemonId_ConfirmsAndReconciles()
    {
        var resolver = Substitute.For<IContainerRuntimeResolver>();
        resolver.ResolveAsync(Arg.Any<ContainerRuntimeSelection?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(ExternalAppEndpointPayloads.Resolution(ContainerRuntimeStatus.DaemonIdentityChanged, pinnedDaemonId: "daemon-old"));
        resolver.ConfirmDaemonIdentityAsync("daemon-abc", Arg.Any<CancellationToken>()).Returns(ExternalAppEndpointPayloads.Resolution());

        var reconciler = Substitute.For<IExternalAppStartupReconciler>();
        reconciler.ReconcileAsync(Arg.Any<CancellationToken>()).Returns(new ExternalAppReconcileSummary
        {
            RowsInspected = 4,
            RowsChanged = 2,
            OrphansRemoved = 0,
            ForeignInstallContainers = 0,
            RowsSkippedBusy = 1
        });

        await using var factory = Factory(resolver: resolver, reconciler: reconciler);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory,
                "POST",
                ExternalAppEndpointPayloads.RuntimeRefresh,
                new
                {
                    acknowledgeDaemonId = "daemon-abc"
                });
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("Ready", document.RootElement.GetProperty("status").GetString(), "the response is the resolution the confirmation produced.");
        await resolver.Received(1).ConfirmDaemonIdentityAsync("daemon-abc", Arg.Any<CancellationToken>());
        await reconciler.Received(1).ReconcileAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The refresh reconciles only after a READY preflight, so it reaches
    ///     <c>IContainerRuntimeResolver.CreateRuntimeAsync</c> — through the reconciler — only in the window where the
    ///     daemon went away between the two calls. Narrow is not unreachable, and the answer there is the same 503 the
    ///     log tail gives, never a 500.
    /// </summary>
    [Test]
    public async Task RefreshRuntime_WhenTheDaemonGoesAwayBeforeTheReconcile_AnswersServiceUnavailable()
    {
        var resolution = ExternalAppEndpointPayloads.Resolution(ContainerRuntimeStatus.DaemonUnreachable, daemonId: null);
        var resolver = Substitute.For<IContainerRuntimeResolver>();
        resolver.ResolveAsync(Arg.Any<ContainerRuntimeSelection?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(ExternalAppEndpointPayloads.Resolution());

        var reconciler = Substitute.For<IExternalAppStartupReconciler>();
        reconciler.ReconcileAsync(Arg.Any<CancellationToken>())
                  .Returns<ExternalAppReconcileSummary>(_ => throw new ContainerRuntimeUnavailableException(resolution));

        await using var factory = Factory(resolver: resolver, reconciler: reconciler);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory, "POST", ExternalAppEndpointPayloads.RuntimeRefresh);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);

        AssertEx.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertEx.Equal(resolution.Message,
            document.RootElement.GetProperty("detail").GetString(),
            "the body carries the resolution's operator message, not a daemon exception message.");
    }

    /// <summary>
    ///     A client echoing the last id it rendered must not be able to approve whatever daemon is answering now — the
    ///     hole a bare boolean leaves open. Nothing is confirmed and nothing is reconciled.
    /// </summary>
    [Test]
    public async Task RefreshRuntime_WithAStaleDaemonId_AnswersBadRequestWithoutConfirming()
    {
        var resolver = Substitute.For<IContainerRuntimeResolver>();
        resolver.ResolveAsync(Arg.Any<ContainerRuntimeSelection?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(ExternalAppEndpointPayloads.Resolution(ContainerRuntimeStatus.DaemonIdentityChanged, daemonId: "daemon-new"));

        var reconciler = Substitute.For<IExternalAppStartupReconciler>();

        await using var factory = Factory(resolver: resolver, reconciler: reconciler);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory,
                "POST",
                ExternalAppEndpointPayloads.RuntimeRefresh,
                new
                {
                    acknowledgeDaemonId = "daemon-abc"
                });

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await resolver.DidNotReceive().ConfirmDaemonIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        AssertEx.Empty(reconciler.ReceivedCalls());
    }

    /// <summary>
    ///     A failed fetch is a 200 carrying the last-good document and the failure, because the catalog the node can
    ///     still serve is the useful answer. <c>refreshFailureMessage</c> is "your click just failed" and
    ///     <c>lastRefreshFailure</c> is "the cache is stale"; they are different sentences and different members.
    /// </summary>
    [Test]
    public async Task RefreshCatalog_WhenTheFetchFails_ReturnsTheLastGoodDocumentAndNamesTheFailure()
    {
        const string Failure = "The catalog host did not answer.";

        var catalog = Substitute.For<IApplicationCatalogProvider>();
        catalog.RefreshAsync(Arg.Any<CancellationToken>())
               .Returns(new ExternalAppCatalogRefreshResult
               {
                   Snapshot = ExternalAppEndpointPayloads.CatalogSnapshot(ExternalAppCatalogSource.RemoteLastGood, Failure),
                   FailureMessage = Failure
               });

        var apps = Substitute.For<IExternalAppService>();
        apps.ListAsync(Arg.Any<CancellationToken>()).Returns([]);

        await using var factory = Factory(apps: apps, catalog: catalog);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory, "POST", ExternalAppEndpointPayloads.CatalogRefresh);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);
        var root = document.RootElement;

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(Failure, root.GetProperty("refreshFailureMessage").GetString());
        AssertEx.Equal(Failure, root.GetProperty("lastRefreshFailure").GetString());
        AssertEx.False(root.GetProperty("fromBundledSeed").GetBoolean(), "a last-good remote document is not the shipped seed.");
        AssertEx.Equal(1, root.GetProperty("applications").GetArrayLength());
    }

    /// <summary>
    ///     A plain GET after a failed refresh reports the stale cache and nothing else: the click that failed was not
    ///     this one, so <c>refreshFailureMessage</c> stays null.
    /// </summary>
    [Test]
    public async Task ListCatalog_AfterAFailedRefresh_CarriesTheStaleCacheFailureOnly()
    {
        const string Failure = "The catalog document failed validation.";

        var catalog = Substitute.For<IApplicationCatalogProvider>();
        catalog.GetCatalogAsync(Arg.Any<CancellationToken>())
               .Returns(ExternalAppEndpointPayloads.CatalogSnapshot(ExternalAppCatalogSource.Bundled, Failure));

        var apps = Substitute.For<IExternalAppService>();
        apps.ListAsync(Arg.Any<CancellationToken>()).Returns([]);

        await using var factory = Factory(apps: apps, catalog: catalog);

        using var response = await ExternalAppEndpointPayloads
            .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.Catalog);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);
        var root = document.RootElement;

        AssertEx.Equal(Failure, root.GetProperty("lastRefreshFailure").GetString());
        AssertEx.Equal(JsonValueKind.Null, root.GetProperty("refreshFailureMessage").ValueKind, "no refresh was attempted by this request.");
        AssertEx.True(root.GetProperty("fromBundledSeed").GetBoolean());
    }

    private static TestServerWebAppFactory Factory(IExternalAppService? apps = null,
        IApplicationCatalogProvider? catalog = null,
        IContainerRuntimeResolver? resolver = null,
        IExternalAppStartupReconciler? reconciler = null) =>
        ExternalAppEndpointPayloads.EnabledFactory(apps ?? Substitute.For<IExternalAppService>(),
            catalog ?? Substitute.For<IApplicationCatalogProvider>(),
            resolver ?? Substitute.For<IContainerRuntimeResolver>(),
            reconciler ?? Substitute.For<IExternalAppStartupReconciler>());
}
