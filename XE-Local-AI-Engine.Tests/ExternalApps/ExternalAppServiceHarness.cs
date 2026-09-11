namespace XE_Local_AI_Engine.Tests.ExternalApps;

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Fake;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     One node's worth of External Apps runtime: a real SQLite-backed instance store, the real service, the real
///     gate and runner, and S0's lying container fake behind a resolver stand-in.
///     <para>
///         The store is the real one on a real database file rather than a substitute, because half of what these
///         tests assert IS the compare-and-swap: a substituted store would happily accept a version no row ever had.
///     </para>
/// </summary>
internal sealed class ExternalAppServiceHarness : IAsyncDisposable
{
    private readonly IOptions<ExternalAppsOptions> _appOptions;
    private readonly IDisposable _hostHandle;
    private readonly Action _stopHost;
    private readonly List<System.Net.Sockets.Socket> _squatters = [];
    private readonly List<string> _restoredOnDispose = [];
    private readonly ServiceProvider _provider;

    private ExternalAppServiceHarness(ServiceProvider provider,
        string rootPath,
        FakeDockerRuntimeClient runtime,
        GatedContainerRuntime gated,
        FakeContainerRuntimeResolver resolver,
        IApplicationCatalogProvider catalog,
        ExternalAppInstanceGate gate,
        ExternalAppOperationRunner runner,
        RecordingExternalAppEventPublisher publisher,
        ManualTimeProvider time,
        ExternalAppService service,
        TestHostLifetime lifetime,
        ExternalAppStorageLayout layout,
        IOptions<ExternalAppsOptions> appOptions,
        RecordingLogger<ExternalAppService> serviceLog)
    {
        // Held as an action and a handle rather than as the lifetime itself: a CancellationToken source in scope
        // makes every store call in this fixture look like one that forgot to thread it.
        _stopHost = lifetime.StopApplication;
        _hostHandle = lifetime;
        _provider = provider;
        RootPath = rootPath;
        Runtime = runtime;
        Gated = gated;
        Resolver = resolver;
        Catalog = catalog;
        Gate = gate;
        Runner = runner;
        Publisher = publisher;
        Time = time;
        Service = service;
        Layout = layout;
        _appOptions = appOptions;
        ServiceLog = serviceLog;
    }

    public string RootPath { get; }

    /// <summary>The real layout the service was built with, so a test can name the paths it is about to plant something on.</summary>
    public ExternalAppStorageLayout Layout { get; }

    public FakeDockerRuntimeClient Runtime { get; }

    public GatedContainerRuntime Gated { get; }

    public FakeContainerRuntimeResolver Resolver { get; }

    public IApplicationCatalogProvider Catalog { get; }

    public ExternalAppInstanceGate Gate { get; }

    public ExternalAppOperationRunner Runner { get; }

    public RecordingExternalAppEventPublisher Publisher { get; }

    public ManualTimeProvider Time { get; }

    public ExternalAppService Service { get; }

    /// <summary>
    ///     Every line the service logged. A secret that reached a log file is as leaked as one returned in a body,
    ///     and only a recording logger can say that it did not.
    /// </summary>
    public RecordingLogger<ExternalAppService> ServiceLog { get; }

    /// <summary>The instance a fixture helper installed, so a test does not thread the id through every call.</summary>
    public Guid InstalledId { get; set; }

    /// <summary>
    ///     What the storage-wipe helper DOES, given the host path it was handed as its one mount, and the exit code
    ///     it finishes with. Null — the default — is the real thing: the contents of that directory go and it exits
    ///     <c>0</c>. A test returning non-zero reaches the "this application's data could not be removed" branch, and
    ///     one returning <c>0</c> without deleting reaches the "it claimed success and something is still there"
    ///     branch. Neither is reachable otherwise without a daemon whose uid mapping can be arranged on demand.
    /// </summary>
    public Func<string, long>? HelperOutcome { get; set; }

    /// <summary>
    ///     Shuts the node down the way the host does, so the operations linked to ApplicationStopping are cancelled.
    /// </summary>
    public void StopHost()
    {
        _stopHost();
    }

    /// <summary>
    ///     The boot reconciler over this harness's own provider, service, gate and runner. Built on demand rather
    ///     than always: most suites never run a pass, and one that did would judge rows the test is still writing.
    /// </summary>
    public ExternalAppStartupReconciler CreateReconciler()
    {
        return new ExternalAppStartupReconciler(_provider.GetRequiredService<IServiceScopeFactory>(),
            Service,
            Layout,
            Gate,
            Runner,
            Publisher,
            _appOptions,
            Time,
            NullLogger<ExternalAppStartupReconciler>.Instance);
    }

    /// <summary>The state observer over the same wiring. Its own <see cref="IDisposable" />: the caller owns it.</summary>
    public ExternalAppStateObserver CreateObserver()
    {
        return new ExternalAppStateObserver(_provider.GetRequiredService<IServiceScopeFactory>(),
            Service,
            Gate,
            Runner,
            Publisher,
            _appOptions,
            Time,
            NullLogger<ExternalAppStateObserver>.Instance);
    }

    /// <summary>
    ///     Drives the row straight to <paramref name="status" /> through the store, so a test can present the boot
    ///     reconciler with a row an engine death would have left — including one whose containers are still running.
    /// </summary>
    public async Task<ExternalAppInstanceSnapshot> ForceStatusAsync(Guid instanceId,
        ExternalAppInstanceStatus status,
        ExternalAppDesiredState? desiredState = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IExternalAppInstanceStore>();
        var row = AssertEx.NotNull(await store.GetAsync(instanceId).ConfigureAwait(false));

        var applied = await store.UpdateStatusAsync(new ExternalAppStatusUpdate(instanceId,
                                          row.Version,
                                          new HashSet<ExternalAppInstanceStatus> { row.Status },
                                          status,
                                          ExternalAppInstanceEventKind.Failed,
                                          EventDetailJson: null,
                                          OccurredAtUtc: 50,
                                          desiredState))
                                 .ConfigureAwait(false);
        AssertEx.True(applied.Applied, $"Forcing instance {instanceId:N} to {status} must not lose its compare-and-swap.");

        return AssertEx.NotNull(await store.GetAsync(instanceId).ConfigureAwait(false));
    }

    public static async Task<ExternalAppServiceHarness> CreateAsync(ApplicationManifest manifest,
        Func<ExternalAppsOptions, ExternalAppsOptions>? configure = null,
        long availableRamBytes = 32L * 1024 * 1024 * 1024)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "xe-ext-apps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        // A record with init-only members, so the hook TRANSFORMS rather than mutates: an Action could not set
        // anything, which is how the previous shape silently ignored every caller that passed one.
        var defaults = new ExternalAppsOptions { Enabled = true, InstanceRoot = rootPath, ServiceReadyTimeoutSeconds = 30 };
        var options = configure is null ? defaults : configure(defaults);

        var runtime = new FakeDockerRuntimeClient(new DockerDaemonEndpoint(new Uri("unix:///xe-external-apps-tests.sock"),
            DockerDaemonEndpointSource.Configuration));
        var gated = new GatedContainerRuntime(runtime);
        var resolver = new FakeContainerRuntimeResolver(gated);
        var catalog = SubstituteCatalog(manifest);

        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(builder => builder.UseSqlite($"Data Source={Path.Combine(rootPath, "node.sqlite")}"));
        services.AddScoped<IExternalAppInstanceStore, ExternalAppInstanceStore>();
        services.AddSingleton(catalog);
        services.AddSingleton<IContainerRuntimeResolver>(resolver);

        var provider = services.BuildServiceProvider(validateScopes: true);
        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
            await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);
        }

        var dataDirectory = new FakeNodeDataDirectory(rootPath);
        var wrapped = Options.Create(options);
        var gate = new ExternalAppInstanceGate();
        var lifetime = new TestHostLifetime();
        var runner = new ExternalAppOperationRunner(provider.GetRequiredService<IServiceScopeFactory>(),
            lifetime,
            NullLogger<ExternalAppOperationRunner>.Instance);
        var publisher = new RecordingExternalAppEventPublisher();
        var time = new ManualTimeProvider();

        var layout = new ExternalAppStorageLayout(dataDirectory, wrapped);
        var serviceLog = new RecordingLogger<ExternalAppService>();
        var service = new ExternalAppService(provider.GetRequiredService<IServiceScopeFactory>(),
            layout,
            new ExternalAppResourceGate(AuditWith(availableRamBytes), dataDirectory),
            gate,
            runner,
            publisher,
            dataDirectory,
            wrapped,
            time,
            serviceLog);

        var harness = new ExternalAppServiceHarness(provider,
            rootPath,
            runtime,
            gated,
            resolver,
            catalog,
            gate,
            runner,
            publisher,
            time,
            service,
            lifetime,
            layout,
            wrapped,
            serviceLog);

        // The storage-wipe helper is the one container the service starts, waits for, and then judges by what is
        // left on disk. The fake would otherwise report it running forever and every reset and uninstall in every
        // suite would wait out its deadline on a clock only a test moves. Installed once, here, so a suite that
        // does not care about the helper never has to know it exists.
        runtime.OneShotExitCode = specification => specification.Labels.ContainsKey(ExternalAppLabels.Helper)
            ? RunHelper(specification, harness.HelperOutcome)
            : null;

        return harness;
    }

    /// <summary>
    ///     Stands in for the container: the helper's one mount is the instance's volumes directory, and what the real
    ///     one runs there is a recursive delete of its CONTENTS, never of the directory itself.
    /// </summary>
    private static long RunHelper(ContainerSpecification specification, Func<string, long>? outcome)
    {
        var mounted = specification.Mounts[0].HostPath;
        if (outcome is not null)
        {
            return outcome(mounted);
        }

        foreach (var directory in Directory.EnumerateDirectories(mounted))
        {
            Directory.Delete(directory, recursive: true);
        }

        foreach (var file in Directory.EnumerateFiles(mounted))
        {
            File.Delete(file);
        }

        return 0;
    }

    /// <summary>
    ///     Writes a row straight through the store and drives it to <paramref name="status" />, so a test can ask what
    ///     the state machine answers from a status no happy path passes through.
    /// </summary>
    public async Task<ExternalAppInstanceSnapshot> SeedAsync(ApplicationManifest manifest,
        ExternalAppInstanceStatus status,
        ExternalAppDesiredState desiredState = ExternalAppDesiredState.Stopped)
    {
        var instanceId = Guid.NewGuid();
        await using var scope = _provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IExternalAppInstanceStore>();

        var created = await store.CreateAsync(new ExternalAppInstanceCreate(instanceId,
                                         manifest.Id,
                                         manifest.ManifestVersion,
                                         System.Text.Json.JsonSerializer.Serialize(manifest, ExternalAppJson.Options),
                                         manifest.DisplayName,
                                         "{}",
                                         Path.Combine(RootPath, "external-apps", "instances", instanceId.ToString("N")),
                                         "docker",
                                         RuntimeOverride: null,
                                         CreatedAtUtc: 1,
                                         ExternalAppInstanceEventKind.PermissionAccepted,
                                         FirstEventDetailJson: null))
                                 .ConfigureAwait(false);

        var version = created.Version;
        var currentStatus = ExternalAppInstanceStatus.Installing;
        if (status != ExternalAppInstanceStatus.Installing)
        {
            var applied = await store.UpdateStatusAsync(new ExternalAppStatusUpdate(instanceId,
                                              version,
                                              new HashSet<ExternalAppInstanceStatus> { currentStatus },
                                              status,
                                              ExternalAppInstanceEventKind.Installed,
                                              EventDetailJson: null,
                                              OccurredAtUtc: 2,
                                              desiredState))
                                     .ConfigureAwait(false);
            AssertEx.True(applied.Applied, "Seeding the row must not lose its compare-and-swap.");
        }

        return AssertEx.NotNull(await store.GetAsync(instanceId).ConfigureAwait(false));
    }

    /// <summary>
    ///     Replaces the stored manifest snapshot, so a test can present a pass with an instance whose recorded
    ///     definition this engine can no longer plan — the shape an older catalog, or a tightened policy, produces.
    /// </summary>
    public Task ReplaceManifestSnapshotAsync(Guid instanceId, ApplicationManifest manifest)
    {
        return ReplaceManifestSnapshotAsync(instanceId, System.Text.Json.JsonSerializer.Serialize(manifest, ExternalAppJson.Options));
    }

    /// <inheritdoc cref="ReplaceManifestSnapshotAsync(Guid, ApplicationManifest)" />
    /// <remarks>The raw form, for the snapshots no <see cref="ApplicationManifest" /> can express — a stored document this engine cannot read back at all.</remarks>
    public async Task ReplaceManifestSnapshotAsync(Guid instanceId, string manifestJson)
    {
        await using var scope = _provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IExternalAppInstanceStore>();
        var row = AssertEx.NotNull(await store.GetAsync(instanceId).ConfigureAwait(false));

        var applied = await store.UpdateStatusAsync(new ExternalAppStatusUpdate(instanceId,
                                          row.Version,
                                          new HashSet<ExternalAppInstanceStatus> { row.Status },
                                          row.Status,
                                          ExternalAppInstanceEventKind.Installed,
                                          EventDetailJson: null,
                                          OccurredAtUtc: 60,
                                          ManifestSnapshotJson: manifestJson))
                                 .ConfigureAwait(false);
        AssertEx.True(applied.Applied, "Replacing the manifest snapshot must not lose its compare-and-swap.");
    }

    /// <summary>
    ///     Moves the row's version on underneath a running operation, so a compare-and-swap the operation is about to
    ///     make loses to a writer it did not expect.
    /// </summary>
    public async Task BumpVersionAsync(Guid instanceId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IExternalAppInstanceStore>();
        var row = AssertEx.NotNull(await store.GetAsync(instanceId).ConfigureAwait(false));

        var applied = await store.UpdateStatusAsync(new ExternalAppStatusUpdate(instanceId,
                                          row.Version,
                                          new HashSet<ExternalAppInstanceStatus> { row.Status },
                                          row.Status,
                                          ExternalAppInstanceEventKind.Failed,
                                          EventDetailJson: null,
                                          OccurredAtUtc: 99))
                                 .ConfigureAwait(false);
        AssertEx.True(applied.Applied, "The competing write must land, or the test is not testing a lost swap.");
    }

    /// <summary>
    ///     Deletes the row and its events while leaving the containers alone — an uninstall that died between those
    ///     two writes, which is the only way to present the boot pass with a genuine orphan.
    /// </summary>
    public async Task DeleteRowAsync(Guid instanceId, long expectedVersion)
    {
        await using var scope = _provider.CreateAsyncScope();
        var deleted = await scope.ServiceProvider.GetRequiredService<IExternalAppInstanceStore>()
                                 .DeleteAsync(instanceId, expectedVersion)
                                 .ConfigureAwait(false);
        AssertEx.True(deleted, $"Deleting the row of instance {instanceId:N} must not lose its compare-and-swap.");
    }

    /// <summary>
    ///     Writes a row straight to the store, bypassing admission, so a test can stage a manifest snapshot no install
    ///     would ever write — a truncated document, or one that deserializes to null. Both are what an interrupted
    ///     write or a hand-edited database leaves behind, and neither can be produced through the service.
    /// </summary>
    public async Task<Guid> CreateRowWithManifestJsonAsync(string applicationId, string displayName, string manifestSnapshotJson)
    {
        var instanceId = Guid.NewGuid();
        await using var scope = _provider.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<IExternalAppInstanceStore>()
                                 .CreateAsync(new ExternalAppInstanceCreate(instanceId,
                                     applicationId,
                                     ManifestVersion: 1,
                                     manifestSnapshotJson,
                                     displayName,
                                     "{}",
                                     Path.Combine(Path.GetTempPath(), "xe-external-apps-corrupt", instanceId.ToString("N")),
                                     "fake",
                                     RuntimeOverride: null,
                                     CreatedAtUtc: 1,
                                     ExternalAppInstanceEventKind.PermissionAccepted,
                                     FirstEventDetailJson: null))
                                 .ConfigureAwait(false);

        AssertEx.True(created.Applied, "The staged row must have been written, or the projection has nothing to degrade.");
        return instanceId;
    }

    /// <summary>Reads the row straight from the database, outside the service, so a projection bug cannot hide a write bug.</summary>
    public async Task<ExternalAppInstanceSnapshot?> ReadAsync(Guid instanceId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IExternalAppInstanceStore>().GetAsync(instanceId).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExternalAppInstanceEventSnapshot>> ReadEventsAsync(Guid instanceId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IExternalAppInstanceStore>()
                          .ListEventsAsync(instanceId, afterSequence: 0, limit: 100)
                          .ConfigureAwait(false);
    }

    /// <summary>
    ///     Waits for the background operation to leave its transient status. A gate on observable state rather than a
    ///     sleep: the pipeline is done exactly when the row says so.
    /// </summary>
    public async Task<ExternalAppInstanceSnapshot> SettleAsync(Guid instanceId, params ExternalAppInstanceStatus[] expected)
    {
        ExternalAppInstanceSnapshot? row = null;
        try
        {
            await AssertEx.EventuallyAsync(() =>
                {
                    row = ReadAsync(instanceId).GetAwaiter().GetResult();
                    return row is not null && Array.IndexOf(expected, row.Status) >= 0;
                },
                TestBudgets.Contended,
                "The instance never reached one of the expected statuses.").ConfigureAwait(false);
        }
        catch (AssertionException)
        {
            // Re-read for the report: the message has to say what the row actually settled on, and the failure
            // category is the whole diagnosis when a pipeline went the wrong way.
            row = await ReadAsync(instanceId).ConfigureAwait(false);
            throw new AssertionException(
                $"Instance {instanceId:N} never reached {string.Join(" or ", expected)}; it is {row?.Status.ToString() ?? "absent"} "
                + $"({row?.FailureCategory?.ToString() ?? "no category"}: {row?.FailureSummary ?? "no summary"}).");
        }

        await WaitUntilIdleAsync(instanceId).ConfigureAwait(false);
        return row!;
    }

    /// <summary>
    ///     Waits until the operation has released the instance gate.
    ///     <para>
    ///         The row settles BEFORE the gate does: the last compare-and-swap happens inside the pipeline and the
    ///         lease is released in the operation's <c>finally</c> afterwards. A test that issued its next command on
    ///         the status alone would race that release and see an "operation in flight" it never caused.
    ///     </para>
    /// </summary>
    public async Task WaitUntilIdleAsync(Guid instanceId)
    {
        await AssertEx.EventuallyAsync(() =>
            {
                var lease = Gate.TryEnterAsync(ExternalAppInstanceGate.InstanceKey(instanceId)).GetAwaiter().GetResult();
                lease?.Dispose();
                return lease is not null;
            },
            TestBudgets.Contended,
            $"The operation on instance {instanceId:N} never released its gate.").ConfigureAwait(false);
    }

    /// <summary>
    ///     Binds and LISTENS on a loopback port so the re-probe the failure translator makes reports it as taken.
    ///     Bound-only would not do it: Linux lets two sockets share an address while neither is listening.
    /// </summary>
    public void Squat(int port)
    {
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
        socket.Listen(backlog: 1);
        _squatters.Add(socket);
    }

    /// <summary>
    ///     Makes a directory undeletable by taking write permission off its parent, which is the only portable way
    ///     on Unix. Restored on disposal so the fixture can still clean up after itself.
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void MakeUndeletable(string path)
    {
        if (Environment.IsPrivilegedProcess)
        {
            // Unix permissions do not bind root, so the directory would be deleted anyway and the test would assert
            // the opposite of what it claims. Visibly skipped rather than silently inverted.
            Skip.Test("This process is privileged; no Unix permission can make a directory undeletable for it.");
        }

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))
                     ?? throw new AssertionException($"'{path}' has no parent directory to lock.");

        _restoredOnDispose.Add(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    public async ValueTask DisposeAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var parent in _restoredOnDispose)
            {
                File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        foreach (var socket in _squatters)
        {
            socket.Dispose();
        }

        _hostHandle.Dispose();
        await _provider.DisposeAsync().ConfigureAwait(false);

        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }

    private static IApplicationCatalogProvider SubstituteCatalog(ApplicationManifest manifest)
    {
        var catalog = Substitute.For<IApplicationCatalogProvider>();
        Seed(catalog, manifest);
        return catalog;
    }

    /// <summary>Replaces what the catalog serves, so a test can move the manifest under an instance.</summary>
    public static void Seed(IApplicationCatalogProvider catalog, params ApplicationManifest[] manifests)
    {
        var document = new ExternalAppCatalogDocument(SchemaVersion: 1, "2026-09-11T00:00:00Z", manifests);
        catalog.GetCatalogAsync(Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(new ExternalAppCatalogSnapshot(document,
                   ExternalAppCatalogSource.Bundled,
                   FetchedAtUtc: null,
                   SourceUrl: null,
                   LastRefreshFailure: null)));
        catalog.GetApplicationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(call => Task.FromResult(
                   manifests.FirstOrDefault(candidate => string.Equals(candidate.Id, call.ArgAt<string>(0), StringComparison.Ordinal))));
    }

    private static IRuntimeDeviceAudit AuditWith(long availableRamBytes)
    {
        var audit = Substitute.For<IRuntimeDeviceAudit>();
        audit.GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new HardwareProfile
             {
                 TotalRamBytes = availableRamBytes,
                 AvailableRamBytes = availableRamBytes,
                 VramBytes = 0,
                 AvailableVramBytes = null,
                 VramKnown = false,
                 GpuVendor = GpuVendor.None,
                 GpuAccelAvailable = false,
                 CpuCores = 8,
                 FreeDiskBytes = 512L * 1024 * 1024 * 1024
             }));

        return audit;
    }
}

/// <summary>
///     A host lifetime a test can actually stop. The substituted one hands out a default CancellationToken, which
///     can never be cancelled, so shutdown behaviour would silently never be exercised.
/// </summary>
internal sealed class TestHostLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource _stopped = new();
    private readonly CancellationTokenSource _stopping = new();

    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => _stopping.Token;

    public CancellationToken ApplicationStopped => _stopped.Token;

    public void StopApplication()
    {
        _stopping.Cancel();
        _stopped.Cancel();
    }

    public void Dispose()
    {
        _stopping.Dispose();
        _stopped.Dispose();
    }
}

/// <summary>
///     Hands out the container fake and reports whatever resolution a test set. Hand-written rather than
///     substituted because <c>CreateRuntimeAsync</c> has a REFUSAL contract — anything but Ready throws carrying the
///     resolution — and a substitute would return null instead, which no caller is written for.
/// </summary>
internal sealed class FakeContainerRuntimeResolver : IContainerRuntimeResolver
{
    private readonly IContainerRuntime _runtime;

    public FakeContainerRuntimeResolver(IContainerRuntime runtime)
    {
        _runtime = runtime;
        Resolution = ReadyResolution(ContainerRuntimeCapabilities.DockerReady, isRootless: true);
    }

    public ContainerRuntimeResolution Resolution { get; set; }

    /// <summary>Thrown instead of handing out a runtime, so a caller's guard around a failing pass can be proven.</summary>
    public Exception? CreateFailure { get; set; }

    public static ContainerRuntimeResolution ReadyResolution(ContainerRuntimeCapabilities capabilities, bool isRootless)
    {
        return Build(ContainerRuntimeStatus.Ready, capabilities, "The container runtime is ready.", isRootless);
    }

    public static ContainerRuntimeResolution UnavailableResolution(string message)
    {
        return Build(ContainerRuntimeStatus.DaemonUnreachable, ContainerRuntimeCapabilities.None, message, isRootless: false);
    }

    public Task<ContainerRuntimeResolution> ResolveAsync(ContainerRuntimeSelection? instanceOverride = null,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Resolution);
    }

    public Task<ContainerRuntimeResolution> ConfirmDaemonIdentityAsync(string expectedDaemonId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Resolution);
    }

    public Task<IContainerRuntime> CreateRuntimeAsync(ContainerRuntimeSelection? instanceOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (CreateFailure is { } failure)
        {
            throw failure;
        }

        return Resolution.Ready
            ? Task.FromResult(_runtime)
            : throw new ContainerRuntimeUnavailableException(Resolution);
    }

    private static ContainerRuntimeResolution Build(ContainerRuntimeStatus status,
        ContainerRuntimeCapabilities capabilities,
        string message,
        bool isRootless)
    {
        return new ContainerRuntimeResolution
        {
            Provider = "docker",
            Status = status,
            Capabilities = capabilities,
            Message = message,
            Daemon = new ContainerDaemonSummary
            {
                Endpoint = "unix:///xe-external-apps-tests.sock",
                EndpointSource = DockerDaemonEndpointSource.Configuration,
                DaemonId = "fake-daemon",
                ServerVersion = "29.7.2",
                ApiVersion = "1.55",
                IsRootless = isRootless
            }
        };
    }
}

/// <summary>
///     S0's fake with one thing it cannot do on its own: hold a pull open until a test releases it. Everything else
///     delegates, so the branches under test are still the ones the repo's own container fake decides.
/// </summary>
internal sealed class GatedContainerRuntime(FakeDockerRuntimeClient inner) : IContainerRuntime
{
    private int _listDetailedCalls;
    private int _mutationCalls;

    /// <summary>Set to hold every pull until the source is completed; null to pull straight through.</summary>
    public TaskCompletionSource? PullGate { get; set; }

    /// <summary>Makes every image look absent, so a rebuild has to pull it again.</summary>
    public bool PretendImagesAreMissing { get; set; }

    /// <summary>Observes each stop in the order the engine issues them.</summary>
    public Action<string>? OnStop { get; set; }

    /// <summary>Observes each start BEFORE it happens, so a test can read what the row said at that moment.</summary>
    public Action<string>? OnStart { get; set; }

    /// <summary>
    ///     Observes each create BEFORE it happens. The seam a test needs to act between two creates of one attempt —
    ///     which is the only place a bind source can be swapped after the storage was prepared.
    /// </summary>
    public Action? OnRun { get; set; }

    /// <summary>
    ///     Observes each detailed listing BEFORE it is served. The reconciler takes its rows first and this listing
    ///     second, so this is where a test can let an operation finish underneath a pass that has already read them.
    /// </summary>
    public Action? OnListDetailed { get; set; }

    /// <summary>
    ///     Fails a removal the way a daemon does when a container will not go. The repo's fake removes whatever it is
    ///     asked for, so a teardown that did not complete has no other way to be modelled.
    /// </summary>
    public Func<string, Exception?>? RemoveFailure { get; set; }

    /// <summary>How many detailed listings this runtime served. The observer's per-tick budget is exactly one.</summary>
    public int ListDetailedCalls => Volatile.Read(ref _listDetailedCalls);

    /// <summary>How many containers were created, started, stopped or removed — what an OBSERVER must never do.</summary>
    public int MutationCalls => Volatile.Read(ref _mutationCalls);

    /// <summary>Completes the first time a pull reaches the gate, so a test can act while the pull is held.</summary>
    public TaskCompletionSource PullReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    ///     Fails a start the way a daemon does when it cannot bind the host port. The repo's fake has no such hook
    ///     because Docker binds on start and nothing below this layer needed to model that.
    /// </summary>
    public Func<string, DockerRuntimeException?>? StartFailure { get; set; }

    public DockerDaemonEndpoint Endpoint => inner.Endpoint;

    public async Task PullImageAsync(string imageReference, IProgress<ContainerPullProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (PullGate is { } gate)
        {
            _ = PullReached.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await inner.PullImageAsync(imageReference, progress, cancellationToken).ConfigureAwait(false);
    }

    public Task<string> RunContainerAsync(ContainerSpecification specification, CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _mutationCalls);
        OnRun?.Invoke();
        return inner.RunContainerAsync(specification, cancellationToken);
    }

    /// <summary>
    ///     Fails an inspect the way a daemon does when the container was removed between a list and this call. The
    ///     repo's fake answers from its own table and has no way to model a container that vanished mid-pass.
    /// </summary>
    public Func<string, Exception?>? InspectFailure { get; set; }

    public Task<ContainerInspection> InspectAsync(string containerId, CancellationToken cancellationToken = default)
    {
        return InspectFailure?.Invoke(containerId) is { } failure
            ? Task.FromException<ContainerInspection>(failure)
            : inner.InspectAsync(containerId, cancellationToken);
    }

    public Task<bool> StopContainerAsync(string containerId, TimeSpan gracePeriod, CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _mutationCalls);
        OnStop?.Invoke(containerId);
        return inner.StopContainerAsync(containerId, gracePeriod, cancellationToken);
    }

    public Task<bool> ImageExistsAsync(string imageReference, CancellationToken cancellationToken = default)
    {
        return PretendImagesAreMissing ? Task.FromResult(false) : inner.ImageExistsAsync(imageReference, cancellationToken);
    }

    public Task<string> CreateNetworkAsync(ContainerNetworkSpecification specification, CancellationToken cancellationToken = default) =>
        inner.CreateNetworkAsync(specification, cancellationToken);

    public Task RemoveNetworkAsync(string networkNameOrId, CancellationToken cancellationToken = default) =>
        inner.RemoveNetworkAsync(networkNameOrId, cancellationToken);

    public Task<IReadOnlyList<string>> ListNetworksAsync(IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken = default) =>
        inner.ListNetworksAsync(labels, cancellationToken);

    public Task<IReadOnlyList<ContainerSummary>> ListContainersDetailedAsync(IReadOnlyDictionary<string, string> labels,
        CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _listDetailedCalls);
        OnListDetailed?.Invoke();
        return inner.ListContainersDetailedAsync(labels, cancellationToken);
    }

    public Task<ContainerLogSnapshot> ReadLogsAsync(string containerId, ContainerLogRequest request, CancellationToken cancellationToken = default) =>
        inner.ReadLogsAsync(containerId, request, cancellationToken);

    public Task<bool> ProbeWritablePathAsync(string containerId, string containerPath, CancellationToken cancellationToken = default) =>
        inner.ProbeWritablePathAsync(containerId, containerPath, cancellationToken);

    public Task<DockerDaemonIdentity> ProbeAsync(CancellationToken cancellationToken = default) =>
        inner.ProbeAsync(cancellationToken);

    public Task<string> CreateContainerAsync(DockerContainerSpecification specification, CancellationToken cancellationToken = default) =>
        inner.CreateContainerAsync(specification, cancellationToken);

    public Task StartContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _mutationCalls);
        OnStart?.Invoke(containerId);
        return StartFailure?.Invoke(containerId) is { } failure
            ? Task.FromException(failure)
            : inner.StartContainerAsync(containerId, cancellationToken);
    }

    public Task<DockerContainerSettings> InspectContainerAsync(string containerId, CancellationToken cancellationToken = default) =>
        inner.InspectContainerAsync(containerId, cancellationToken);

    public Task<IReadOnlyList<string>> ListContainersAsync(IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken = default) =>
        inner.ListContainersAsync(labels, cancellationToken);

    public Task RemoveContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _mutationCalls);
        return RemoveFailure?.Invoke(containerId) is { } failure
            ? Task.FromException(failure)
            : inner.RemoveContainerAsync(containerId, cancellationToken);
    }

    public Task<DockerExecutionOutcome> ExecuteAsync(string containerId, DockerExecutionRequest request, CancellationToken cancellationToken = default) =>
        inner.ExecuteAsync(containerId, request, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Records every ping and every progress report, so the event feed is asserted rather than assumed.</summary>
internal sealed class RecordingExternalAppEventPublisher : IExternalAppEventPublisher
{
    private readonly ConcurrentQueue<(Guid InstanceId, long Sequence, ExternalAppInstanceEventKind Kind, ExternalAppInstanceStatus Status)> _events = new();
    private readonly ConcurrentQueue<(Guid InstanceId, string Service, int LayerCount, int CompletedLayers, long Bytes)> _pullProgress = new();

    public IReadOnlyList<(Guid InstanceId, long Sequence, ExternalAppInstanceEventKind Kind, ExternalAppInstanceStatus Status)> Events => [.. _events];

    public IReadOnlyList<(Guid InstanceId, string Service, int LayerCount, int CompletedLayers, long Bytes)> PullProgress => [.. _pullProgress];

    public Task PublishAsync(Guid instanceId,
        long sequence,
        ExternalAppInstanceEventKind kind,
        ExternalAppInstanceStatus status,
        CancellationToken cancellationToken = default)
    {
        _events.Enqueue((instanceId, sequence, kind, status));
        return Task.CompletedTask;
    }

    public Task PublishPullProgressAsync(Guid instanceId,
        string service,
        int layerCount,
        int completedLayers,
        long bytes,
        CancellationToken cancellationToken = default)
    {
        _pullProgress.Enqueue((instanceId, service, layerCount, completedLayers, bytes));
        return Task.CompletedTask;
    }
}
