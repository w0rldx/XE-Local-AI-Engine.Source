namespace XE_Local_AI_Engine.Tests.ExternalApps;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Start, stop, restart, reset and uninstall: the state machine's refusals, the reuse-or-rebuild decision, and
///     what each pipeline leaves on disk.
/// </summary>
public sealed class ExternalAppServiceLifecycleTests
{
    [Test]
    public async Task Stop_StopsTheContainersInReverseDependencyOrder()
    {
        await using var harness = await RunningHarnessAsync(TwoServiceManifest()).ConfigureAwait(false);
        var installed = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        var stopOrder = new List<string>();
        harness.Gated.OnStop = containerId =>
            stopOrder.Add(harness.Runtime.InspectAsync(containerId).GetAwaiter().GetResult().Name);

        _ = await harness.Service.StopAsync(installed.Id, installed.Version).ConfigureAwait(false);
        var row = await harness.SettleAsync(installed.Id, ExternalAppInstanceStatus.Stopped).ConfigureAwait(false);

        AssertEx.Equal(ExternalAppDesiredState.Stopped, row.DesiredState);
        AssertEx.True(row.StoppedAtUtc.HasValue, "A stop records when it happened.");

        // "sidecar" depends on "web", so the dependant goes first: stopping what it depends on underneath it is how
        // a clean shutdown turns into a crash log.
        AssertEx.Equal(expected: 2, stopOrder.Count);
        AssertEx.True(stopOrder[0].EndsWith("-sidecar", StringComparison.Ordinal), $"'{stopOrder[0]}' was stopped first; the dependant must be.");
        AssertEx.True(stopOrder[1].EndsWith("-web", StringComparison.Ordinal), $"'{stopOrder[1]}' was stopped last; the dependency must be.");

        // Containers and the network are KEPT: stop is not a teardown.
        AssertEx.Empty(harness.Runtime.RemovedContainerIds);
        AssertEx.NotEmpty(harness.Runtime.CreatedNetworks);
    }

    [Test]
    public async Task Stop_OnAnAlreadyStoppedInstance_IsIdempotentAndWritesNoEvent()
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest()).ConfigureAwait(false);
        var installed = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        _ = await harness.Service.StopAsync(installed.Id, installed.Version).ConfigureAwait(false);
        var stopped = await harness.SettleAsync(installed.Id, ExternalAppInstanceStatus.Stopped).ConfigureAwait(false);
        var eventsBefore = (await harness.ReadEventsAsync(installed.Id).ConfigureAwait(false)).Count;

        var again = await harness.Service.StopAsync(installed.Id, stopped.Version).ConfigureAwait(false);

        AssertEx.Equal(ExternalAppInstanceStatus.Stopped, again.Status);
        AssertEx.Equal(stopped.Version, again.Version);
        AssertEx.Equal(eventsBefore, (await harness.ReadEventsAsync(installed.Id).ConfigureAwait(false)).Count);
    }

    [Test]
    public async Task Start_WithOneServiceContainerMissing_RebuildsTheWholeInstance()
    {
        await using var harness = await StoppedHarnessAsync(TwoServiceManifest()).ConfigureAwait(false);
        var stopped = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        var survivor = harness.Runtime.CreatedContainerIds[0];
        await harness.Runtime.RemoveContainerAsync(harness.Runtime.CreatedContainerIds[1]).ConfigureAwait(false);
        var createdBefore = harness.Runtime.CreatedContainerIds.Count;

        _ = await harness.Service.StartAsync(stopped.Id, stopped.Version).ConfigureAwait(false);
        _ = await harness.SettleAsync(stopped.Id, ExternalAppInstanceStatus.Running).ConfigureAwait(false);

        // Both services were recreated. Starting the surviving subset would have reported a half-broken application
        // as Running.
        AssertEx.Equal(createdBefore + 2, harness.Runtime.CreatedContainerIds.Count);
        AssertEx.Contains(harness.Runtime.RemovedContainerIds, survivor);
    }

    [Test]
    public async Task Start_WhenTheImageIsGone_PullsItAgain()
    {
        await using var harness = await StoppedHarnessAsync(SingleServiceManifest()).ConfigureAwait(false);
        var stopped = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        await harness.Runtime.RemoveContainerAsync(harness.Runtime.CreatedContainerIds[0]).ConfigureAwait(false);
        harness.Gated.PretendImagesAreMissing = true;
        var pullsBefore = harness.Runtime.PulledImages.Count;

        _ = await harness.Service.StartAsync(stopped.Id, stopped.Version).ConfigureAwait(false);
        _ = await harness.SettleAsync(stopped.Id, ExternalAppInstanceStatus.Running).ConfigureAwait(false);

        // The rebuild starts at the pull precisely because a stopped instance's image can be gone by the time it is
        // started again.
        AssertEx.Equal(pullsBefore + 1, harness.Runtime.PulledImages.Count);
    }

    [Test]
    public async Task Start_AfterConfigure_RecreatesTheContainersAndTheChangedVariableReachesTheEnvironment()
    {
        var manifest = ExternalAppTestManifests.Manifest(
            [ExternalAppTestManifests.Service("web", environment: new Dictionary<string, string>(StringComparer.Ordinal) { ["HOST"] = "${LLM_HOST}" })],
            variables: [ExternalAppTestManifests.Variable("LLM_HOST", @default: "http://localhost:11434")]);

        await using var harness = await StoppedHarnessAsync(manifest).ConfigureAwait(false);
        var stopped = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        var detail = await harness.Service.ConfigureAsync(stopped.Id,
                                       stopped.Version,
                                       new Dictionary<string, string>(StringComparer.Ordinal) { ["LLM_HOST"] = "http://127.0.0.1:9999" })
                                   .ConfigureAwait(false);

        AssertEx.True(detail.NeedsRecreate, "A configure records that the running containers no longer match the stored values.");

        var specifications = new List<ContainerSpecification>();
        harness.Runtime.RunFailure = specification =>
        {
            specifications.Add(specification);
            return null;
        };

        _ = await harness.Service.StartAsync(stopped.Id, detail.Summary.Version).ConfigureAwait(false);
        var row = await harness.SettleAsync(stopped.Id, ExternalAppInstanceStatus.Running).ConfigureAwait(false);

        AssertEx.NotEmpty(specifications, "The flag forces a recreate; reusing the container would keep the old environment.");
        AssertEx.Equal("http://127.0.0.1:9999", specifications[^1].Environment["HOST"]);
        AssertEx.False(row.NeedsRecreate, "The running compare-and-swap clears the flag.");
    }

    /// <summary>
    ///     The reuse check compares the observed image EXACTLY against the installed reference. A different
    ///     reference, a bare image id and an empty string all mean the same thing here: a container whose
    ///     provenance this instance cannot establish, which is rebuilt rather than started.
    /// </summary>
    [Test]
    [Arguments(ExternalAppTestManifests.SecondImage)]
    [Arguments("sha256:9db7b59979c38555a39def84a31fb98b5296952f9e3afd4f6f11f05b07adfab0")]
    [Arguments("")]
    public async Task Start_WhenAnExistingContainersImageIsNotTheInstalledReference_Rebuilds(string observed)
    {
        await using var harness = await StoppedHarnessAsync(SingleServiceManifest()).ConfigureAwait(false);
        var stopped = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));
        var createdBefore = harness.Runtime.CreatedContainerIds.Count;

        // The container is there, but the daemon does not report the image the snapshot names.
        harness.Runtime.InspectionMutator = inspection => inspection with { Image = observed };

        _ = await harness.Service.StartAsync(stopped.Id, stopped.Version).ConfigureAwait(false);
        _ = await harness.SettleAsync(stopped.Id, ExternalAppInstanceStatus.Running).ConfigureAwait(false);

        AssertEx.Equal(createdBefore + 1, harness.Runtime.CreatedContainerIds.Count);
    }

    /// <summary>The other half of the same rule: the reference the instance installed IS reused, never rebuilt.</summary>
    [Test]
    public async Task Start_WhenTheExistingContainerReportsTheInstalledReference_ReusesIt()
    {
        await using var harness = await StoppedHarnessAsync(SingleServiceManifest()).ConfigureAwait(false);
        var stopped = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));
        var createdBefore = harness.Runtime.CreatedContainerIds.Count;

        _ = await harness.Service.StartAsync(stopped.Id, stopped.Version).ConfigureAwait(false);
        _ = await harness.SettleAsync(stopped.Id, ExternalAppInstanceStatus.Running).ConfigureAwait(false);

        AssertEx.Equal(createdBefore, harness.Runtime.CreatedContainerIds.Count, "A verified container is started again, not replaced.");
        AssertEx.Empty(harness.Runtime.RemovedContainerIds);
    }

    [Test]
    public async Task Restart_EmitsRestartedAndLeavesTheInstanceRunning()
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest()).ConfigureAwait(false);
        var installed = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        _ = await harness.Service.RestartAsync(installed.Id, installed.Version).ConfigureAwait(false);
        var row = await harness.SettleAsync(installed.Id, ExternalAppInstanceStatus.Running).ConfigureAwait(false);

        AssertEx.Equal(ExternalAppDesiredState.Running, row.DesiredState);
        var kinds = (await harness.ReadEventsAsync(installed.Id).ConfigureAwait(false)).Select(entry => entry.Kind).ToList();
        AssertEx.Contains(kinds, ExternalAppInstanceEventKind.Restarted);
    }

    [Test]
    public async Task Reset_WipesTheVolumesReMaterialisesTheFilesAndRestoresTheDesiredState()
    {
        var manifest = ExternalAppTestManifests.Manifest(
        [
            ExternalAppTestManifests.Service("web",
                storage: [new ApplicationStorage("data", "/var/lib/app")],
                files: [ExternalAppTestManifests.File("settings.yml", "/etc/app/settings.yml", "server: local\n")])
        ]);

        await using var harness = await StoppedHarnessAsync(manifest).ConfigureAwait(false);
        var stopped = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        var volumeFile = Path.Combine(stopped.StoragePath, "volumes", "web", "data", "user-data.txt");
        await File.WriteAllTextAsync(volumeFile, "something the user made").ConfigureAwait(false);
        var materialised = Path.Combine(stopped.StoragePath, "files", "web", "settings.yml");
        File.Delete(materialised);

        _ = await harness.Service.ResetAsync(stopped.Id, stopped.Version).ConfigureAwait(false);
        var row = await harness.SettleAsync(stopped.Id, ExternalAppInstanceStatus.Stopped).ConfigureAwait(false);

        AssertEx.False(File.Exists(volumeFile), "A reset wipes the instance's volumes; that is what it is for.");
        AssertEx.True(File.Exists(materialised), "A reset repairs the catalog assets it finds missing.");

        // The desired state is the user's. A reset is not a decision to start something that was stopped.
        AssertEx.Equal(ExternalAppDesiredState.Stopped, row.DesiredState);
    }

    [Test]
    public async Task Reset_WhenTheVolumeWipeFails_LandsOnFailedRatherThanStayingInResetting()
    {
        await using var harness = await StoppedHarnessAsync(
                                            ExternalAppTestManifests.Manifest(
                                                [ExternalAppTestManifests.Service("web", storage: [new ApplicationStorage("data", "/var/lib/app")])]))
                                        .ConfigureAwait(false);
        var stopped = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        var volumes = Path.Combine(stopped.StoragePath, "volumes");
        Directory.Delete(volumes, recursive: true);
        await File.WriteAllTextAsync(volumes, "a file where the volumes directory belongs").ConfigureAwait(false);

        _ = await harness.Service.ResetAsync(stopped.Id, stopped.Version).ConfigureAwait(false);
        var row = await harness.SettleAsync(stopped.Id, ExternalAppInstanceStatus.Failed).ConfigureAwait(false);

        // Resetting is an absorbing status where every operation answers 409, another reset included.
        AssertEx.Equal(ExternalAppFailureCategory.StorageError, row.FailureCategory);
    }

    [Test]
    public async Task Uninstall_RemovesTheContainersTheNetworkTheRowsAndTheDirectory()
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest()).ConfigureAwait(false);
        var installed = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        _ = await harness.Service.UninstallAsync(installed.Id, installed.Version).ConfigureAwait(false);
        await AssertEx.EventuallyAsync(() => harness.ReadAsync(installed.Id).GetAwaiter().GetResult() is null,
            TestBudgets.Contended,
            "The uninstall never deleted the instance row.").ConfigureAwait(false);

        // An absent row is not the end of the pipeline: the event and the gate's forget still follow it. The
        // runner's own map is what says the operation is over — and unlike the gate helper, asking it re-creates no
        // entry the count below is about to measure.
        await AssertEx.EventuallyAsync(() => !harness.Runner.IsRunning(installed.Id),
            TestBudgets.Contended,
            "The uninstall operation never finished.").ConfigureAwait(false);

        AssertEx.NotEmpty(harness.Runtime.RemovedContainerIds);
        AssertEx.Empty(harness.Runtime.CreatedNetworks);
        AssertEx.False(Directory.Exists(installed.StoragePath), "The uninstall deletes the instance's data directory.");

        // The instance key was forgotten. The one entry left is the application key install takes transiently, which
        // is bounded by the catalog rather than by how many instances have ever existed.
        AssertEx.Equal(expected: 1, harness.Gate.TrackedCount);
        using var reentered = AssertEx.NotNull(
            await harness.Gate.TryEnterAsync(ExternalAppInstanceGate.InstanceKey(installed.Id)).ConfigureAwait(false),
            "The uninstalled instance's gate must be free.");
        AssertEx.Equal(expected: 2,
            harness.Gate.TrackedCount,
            "Re-entering the instance key created a NEW entry, which is what proves the old one was dropped.");
    }

    [Test]
    public async Task Uninstall_WhenTheDirectoryCannotBeDeleted_StillRemovesTheRows()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test("Making a directory undeletable needs Unix permissions on its parent.");
            return;
        }

        await using var harness = await RunningHarnessAsync(SingleServiceManifest()).ConfigureAwait(false);
        var installed = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        // An empty directory tree that cannot be removed is recoverable by hand. A row the page is stuck on is not,
        // which is why a failed directory delete is a warning and the rows go anyway. What does NOT go anyway is an
        // instance whose DATA is still there: that is the wipe above it, and it throws.
        harness.MakeUndeletable(installed.StoragePath);

        _ = await harness.Service.UninstallAsync(installed.Id, installed.Version).ConfigureAwait(false);
        await AssertEx.EventuallyAsync(() => harness.ReadAsync(installed.Id).GetAwaiter().GetResult() is null,
            TestBudgets.Contended,
            "The rows must go even when the directory cannot.").ConfigureAwait(false);
        await AssertEx.EventuallyAsync(() => !harness.Runner.IsRunning(installed.Id),
            TestBudgets.Contended,
            "The uninstall operation never finished.").ConfigureAwait(false);

        // Without this the test passes on a host where the directory was deletable all along, which proves only that
        // an ordinary uninstall removes its rows.
        AssertEx.True(Directory.Exists(installed.StoragePath),
            "The fixture failed to make the directory undeletable, so this proves nothing about the order of the two deletes.");
    }

    /// <summary>
    ///     The row and the directory go only once the containers are CONFIRMED gone. Deleting them while a container
    ///     of this instance survives would leave it running with nothing left that could act on it, and the boot pass
    ///     would then see only an orphan.
    /// </summary>
    [Test]
    public async Task Uninstall_WhenAContainerCannotBeRemoved_KeepsTheRowAndTheStorage()
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest()).ConfigureAwait(false);
        var installed = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        harness.Gated.RemoveFailure = static _ => new DockerRuntimeException("The daemon refused to remove the container.");

        _ = await harness.Service.UninstallAsync(installed.Id, installed.Version).ConfigureAwait(false);
        var row = await harness.SettleAsync(installed.Id, ExternalAppInstanceStatus.Failed).ConfigureAwait(false);

        AssertEx.Equal(ExternalAppFailureCategory.Unknown, row.FailureCategory);
        AssertEx.True(Directory.Exists(installed.StoragePath), "An uninstall that could not remove the containers must keep the instance's data.");
        AssertEx.NotEmpty(await harness.ReadEventsAsync(installed.Id).ConfigureAwait(false));
    }

    /// <summary>
    ///     The window the storage layout's one-time validation cannot cover: the plan is path strings, and the second
    ///     service's bind source is only bound after the first container already exists. The check runs immediately
    ///     before EACH create, so a component swapped for a link in between is refused with nothing started.
    /// </summary>
    [Test]
    public async Task Install_WhenABindSourceIsSwappedForALinkBetweenTwoCreates_FailsAsAStorageErrorBeforeTheSecondCreate()
    {
        SymlinkSupport.EnsureSupported();

        var manifest = ExternalAppTestManifests.Manifest(
        [
            ExternalAppTestManifests.Service("web", storage: [new ApplicationStorage("data", "/data")]),
            ExternalAppTestManifests.Service("sidecar",
                storage: [new ApplicationStorage("data", "/data")],
                dependsOn: [new ApplicationDependency("web", "started")],
                image: ExternalAppTestManifests.SecondImage)
        ]);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest).ConfigureAwait(false);
        var elsewhere = Path.Combine(harness.RootPath, "outside-the-instance");
        _ = Directory.CreateDirectory(elsewhere);

        var swapped = 0;
        harness.Gated.OnRun = () =>
        {
            if (Interlocked.Exchange(ref swapped, value: 1) != 0)
            {
                return;
            }

            // Between the first create and the second, which is where the prepared tree stops being evidence.
            var instanceRoot = Directory.GetDirectories(Path.Combine(harness.RootPath, "external-apps", "instances")).Single();
            var source = Path.Combine(instanceRoot, "volumes", "sidecar", "data");
            Directory.Delete(source);
            Directory.CreateSymbolicLink(source, elsewhere);
        };

        var admitted = await harness.Service.InstallAsync(new InstallCommand(manifest.Id,
                                        DisplayName: null,
                                        manifest.ManifestVersion,
                                        manifest.ManifestSha256,
                                        new Dictionary<string, string>(StringComparer.Ordinal),
                                        AcceptPermissions: true))
                                    .ConfigureAwait(false);

        var row = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Failed).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, swapped, "The fixture never swapped the bind source, so this proves nothing.");
        AssertEx.Equal(ExternalAppFailureCategory.StorageError, row.FailureCategory);
        AssertEx.Equal(expected: 1,
            harness.Runtime.CreatedContainerIds.Count,
            "The second container was created over the link; the refusal has to come BEFORE the create, not after it.");
    }

    [Test]
    [Arguments("Start")]
    [Arguments("Stop")]
    [Arguments("Restart")]
    [Arguments("Reset")]
    [Arguments("Uninstall")]
    public async Task AnyLifecycleCommand_WithAStaleExpectedVersion_Is409VersionConflict(string operation)
    {
        await using var harness = await ExternalAppServiceHarness.CreateAsync(SingleServiceManifest()).ConfigureAwait(false);
        var row = await harness.SeedAsync(SingleServiceManifest(), ExternalAppInstanceStatus.Stopped).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<ExternalAppConcurrencyException>(
            () => Invoke(harness, operation, row.Id, row.Version + 1)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Every refusing cell of the transition table. A status a command is not defined for is an invalid
    ///     transition, not a command that quietly does nothing.
    /// </summary>
    [Test]
    [Arguments(ExternalAppInstanceStatus.Running, "Start")]
    [Arguments(ExternalAppInstanceStatus.Installing, "Start")]
    [Arguments(ExternalAppInstanceStatus.Installing, "Stop")]
    [Arguments(ExternalAppInstanceStatus.Installing, "Restart")]
    [Arguments(ExternalAppInstanceStatus.Installing, "Reset")]
    [Arguments(ExternalAppInstanceStatus.Installing, "Uninstall")]
    [Arguments(ExternalAppInstanceStatus.Updating, "Start")]
    [Arguments(ExternalAppInstanceStatus.Updating, "Uninstall")]
    [Arguments(ExternalAppInstanceStatus.Resetting, "Stop")]
    [Arguments(ExternalAppInstanceStatus.Starting, "Start")]
    [Arguments(ExternalAppInstanceStatus.Starting, "Stop")]
    [Arguments(ExternalAppInstanceStatus.Stopping, "Restart")]
    [Arguments(ExternalAppInstanceStatus.Uninstalling, "Uninstall")]
    public async Task ARefusedCellOfTheTransitionTable_Is409InvalidTransition(ExternalAppInstanceStatus status, string operation)
    {
        await using var harness = await ExternalAppServiceHarness.CreateAsync(SingleServiceManifest()).ConfigureAwait(false);
        var row = await harness.SeedAsync(SingleServiceManifest(), status).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<ExternalAppInvalidTransitionException>(
            () => Invoke(harness, operation, row.Id, row.Version)).ConfigureAwait(false);
    }

    /// <summary>Cancel is accepted on the three long transients only, and only when something is actually running.</summary>
    [Test]
    [Arguments(ExternalAppInstanceStatus.Stopped)]
    [Arguments(ExternalAppInstanceStatus.Running)]
    [Arguments(ExternalAppInstanceStatus.Failed)]
    [Arguments(ExternalAppInstanceStatus.Starting)]
    [Arguments(ExternalAppInstanceStatus.Stopping)]
    [Arguments(ExternalAppInstanceStatus.Uninstalling)]
    public async Task Cancel_OnAStatusItIsNotDefinedFor_Is409InvalidTransition(ExternalAppInstanceStatus status)
    {
        await using var harness = await ExternalAppServiceHarness.CreateAsync(SingleServiceManifest()).ConfigureAwait(false);
        var row = await harness.SeedAsync(SingleServiceManifest(), status).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<ExternalAppInvalidTransitionException>(
            () => harness.Service.CancelAsync(row.Id)).ConfigureAwait(false);
    }

    [Test]
    public async Task Cancel_OnATransientRowWithNoRunningOperation_Is409InvalidTransition()
    {
        await using var harness = await ExternalAppServiceHarness.CreateAsync(SingleServiceManifest()).ConfigureAwait(false);
        var row = await harness.SeedAsync(SingleServiceManifest(), ExternalAppInstanceStatus.Installing).ConfigureAwait(false);

        // A transient status with no live operation is a CRASHED operation. The boot reconciler settles those.
        _ = await AssertEx.ThrowsAsync<ExternalAppInvalidTransitionException>(
            () => harness.Service.CancelAsync(row.Id)).ConfigureAwait(false);
    }

    [Test]
    public async Task Configure_OnARunningInstance_Is409InvalidTransition()
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest()).ConfigureAwait(false);
        var installed = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        _ = await AssertEx.ThrowsAsync<ExternalAppInvalidTransitionException>(
            () => harness.Service.ConfigureAsync(installed.Id, installed.Version, new Dictionary<string, string>(StringComparer.Ordinal)))
            .ConfigureAwait(false);
    }

    [Test]
    public async Task Configure_WithTheMaskSentinel_KeepsTheStoredSecretAndNeverReturnsIt()
    {
        var manifest = ExternalAppTestManifests.Manifest(
            [ExternalAppTestManifests.Service("web", environment: new Dictionary<string, string>(StringComparer.Ordinal) { ["PW"] = "${ADMIN_PASSWORD}" })],
            variables: [ExternalAppTestManifests.Variable("ADMIN_PASSWORD", required: true, type: "secret")]);

        await using var harness = await StoppedHarnessAsync(manifest,
                                            new Dictionary<string, string>(StringComparer.Ordinal) { ["ADMIN_PASSWORD"] = "correct horse battery staple" })
                                        .ConfigureAwait(false);
        var stopped = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        var detail = await harness.Service.ConfigureAsync(stopped.Id,
                                       stopped.Version,
                                       new Dictionary<string, string>(StringComparer.Ordinal) { ["ADMIN_PASSWORD"] = ExternalAppVariableMask.Value })
                                   .ConfigureAwait(false);

        AssertEx.Equal(ExternalAppVariableMask.Value, detail.MaskedVariables["ADMIN_PASSWORD"]);

        var row = AssertEx.NotNull(await harness.ReadAsync(stopped.Id).ConfigureAwait(false));
        AssertEx.Contains(row.VariablesJson, "correct horse battery staple");
    }

    private static Task Invoke(ExternalAppServiceHarness harness, string operation, Guid instanceId, long expectedVersion)
    {
        return operation switch
        {
            "Start" => harness.Service.StartAsync(instanceId, expectedVersion),
            "Stop" => harness.Service.StopAsync(instanceId, expectedVersion),
            "Restart" => harness.Service.RestartAsync(instanceId, expectedVersion),
            "Reset" => harness.Service.ResetAsync(instanceId, expectedVersion),
            "Uninstall" => harness.Service.UninstallAsync(instanceId, expectedVersion),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "No such lifecycle command.")
        };
    }

    private static ApplicationManifest SingleServiceManifest()
    {
        return ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")]);
    }

    private static ApplicationManifest TwoServiceManifest()
    {
        return ExternalAppTestManifests.Manifest(
        [
            ExternalAppTestManifests.Service("web", ports: [ExternalAppTestManifests.UiPort(8080)]),
            ExternalAppTestManifests.Service("sidecar",
                dependsOn: [new ApplicationDependency("web", "started")],
                image: ExternalAppTestManifests.SecondImage)
        ]);
    }

    private static async Task<ExternalAppServiceHarness> RunningHarnessAsync(ApplicationManifest manifest,
        IReadOnlyDictionary<string, string>? variables = null)
    {
        var harness = await ExternalAppServiceHarness.CreateAsync(manifest).ConfigureAwait(false);
        var admitted = await harness.Service.InstallAsync(new InstallCommand(manifest.Id,
                                        DisplayName: null,
                                        manifest.ManifestVersion,
                                        manifest.ManifestSha256,
                                        variables ?? new Dictionary<string, string>(StringComparer.Ordinal),
                                        AcceptPermissions: true))
                                    .ConfigureAwait(false);

        _ = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Running).ConfigureAwait(false);
        harness.InstalledId = admitted.Id;
        return harness;
    }

    private static async Task<ExternalAppServiceHarness> StoppedHarnessAsync(ApplicationManifest manifest,
        IReadOnlyDictionary<string, string>? variables = null)
    {
        var harness = await RunningHarnessAsync(manifest, variables).ConfigureAwait(false);
        var running = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        _ = await harness.Service.StopAsync(running.Id, running.Version).ConfigureAwait(false);
        _ = await harness.SettleAsync(running.Id, ExternalAppInstanceStatus.Stopped).ConfigureAwait(false);
        return harness;
    }
}
