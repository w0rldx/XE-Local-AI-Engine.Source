namespace XE_Local_AI_Engine.Tests.ExternalApps;

using System.Globalization;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The short-lived engine-owned container that deletes an instance's volume CONTENTS, and what happens when it
///     cannot.
///     <para>
///         The engine itself cannot always do this: an application whose image runs as its own non-root user leaves
///         <c>0700</c> directories owned by a uid that is not the engine's, and a host-side recursive delete on them
///         fails. Everything here is about the container that can — that it is confined to one bind mount of one
///         instance's volumes directory, that it is removed whatever happens, and that a wipe which did not finish
///         stops the operation rather than letting an uninstall delete the row over data still on disk.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ExternalAppStorageHelperTests
{
    private const string VolumeName = "data";
    private const string ServiceName = "web";

    /// <summary>
    ///     A digest-pinned reference that is NOT the option's default, so the assertion below proves the helper runs
    ///     the configured image rather than one hardcoded beside it.
    /// </summary>
    private const string HelperImage = "ghcr.io/example/helper@sha256:00000000000000000000000000000000000000000000000000000000000000aa";

    [Test]
    public async Task Reset_WipesTheVolumesThroughAHelperConfinedToOneWritableBindOfTheInstancesVolumes()
    {
        await using var harness = await StoppedHarnessAsync();
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));
        var volumes = Path.Combine(row.StoragePath, "volumes");
        await File.WriteAllTextAsync(Path.Combine(volumes, ServiceName, VolumeName, "user-data.txt"), "written by the application");

        var helper = CaptureHelperSpecification(harness);

        _ = await harness.Service.ResetAsync(row.Id, row.Version);
        _ = await harness.SettleAsync(row.Id, ExternalAppInstanceStatus.Stopped);

        var specification = AssertEx.NotNull(helper.Value, "The reset never created a storage helper container.");

        // The image is the operator-visible option, and it is digest-pinned. A tag here would let a different image
        // run as root over an application's data.
        AssertEx.Equal(HelperImage, specification.Image);

        // ONE mount, writable, and it is this instance's volumes directory. This is the whole confinement: whatever
        // the container's command were, it could reach nothing else.
        var mount = AssertEx.NotNull(specification.Mounts.Count == 1 ? specification.Mounts[0] : null,
            $"The helper was given {specification.Mounts.Count} mounts; exactly one is the confinement.");
        AssertEx.Equal(volumes, mount.HostPath);
        AssertEx.Equal("/storage", mount.ContainerPath);
        AssertEx.False(mount.ReadOnly, "The helper must be able to delete through its mount.");

        // Nothing else is granted. A GPU is not asserted against because it is unrepresentable at this layer: the
        // specification has no device member at all, which is a stronger guarantee than a check.
        AssertEx.Empty(specification.PublishedPorts, "The helper publishes nothing.");
        AssertEx.Empty(specification.CapabilitiesToAdd, "The helper adds no capability to Docker's default set.");
        AssertEx.Empty(specification.ExtraHosts);
        AssertEx.Equal("none", specification.NetworkName, "The helper joins Docker's isolated network and never the instance's.");
        AssertEx.Equal(ContainerRestartMode.None, specification.RestartMode, "A one-shot container the daemon restarted would wipe the rebuilt volumes.");
        AssertEx.True(specification.ReadOnlyRootFilesystem, "The helper writes nothing outside its mount.");
        AssertEx.Null(specification.User, "In-container root is the only identity that can unlink what a non-root application user left.");
        AssertEx.Equal(expected: 0L, specification.MemoryBytes);
        AssertEx.Equal(expected: 0L, specification.NanoCpus);

        // The command is a constant: no instance id, no path and no name from the engine is interpolated into it.
        var command = AssertEx.NotNull(specification.Command, "The helper overrides the image's command.");
        AssertEx.Equal(expected: 3, command.Count);
        AssertEx.Equal("sh", command[0]);
        AssertEx.Equal("-c", command[1]);
        AssertEx.Equal("find /storage -mindepth 1 -delete", command[2]);

        // The instance's own three labels, so a teardown and the boot pass's orphan sweep both account for it, plus
        // the helper label. No service label: reuse, the observer and the reconciler all key on that one.
        AssertEx.Equal(ExternalAppLabels.OwnerValue, specification.Labels[ExternalAppLabels.Owner]);
        AssertEx.Equal(harness.Service.InstallId, specification.Labels[ExternalAppLabels.Install]);
        AssertEx.Equal(row.Id.ToString("N", CultureInfo.InvariantCulture), specification.Labels[ExternalAppLabels.Instance]);
        AssertEx.Equal(ExternalAppLabels.StorageWipeValue, specification.Labels[ExternalAppLabels.Helper]);
        AssertEx.False(specification.Labels.ContainsKey(ExternalAppLabels.Service),
            "A helper carrying a service label would be read as one of the application's own containers.");
    }

    [Test]
    public async Task Reset_RemovesTheHelperAfterwards()
    {
        await using var harness = await StoppedHarnessAsync();
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));

        _ = await harness.Service.ResetAsync(row.Id, row.Version);
        _ = await harness.SettleAsync(row.Id, ExternalAppInstanceStatus.Stopped);

        await AssertNoHelperIsLeftAsync(harness, row.Id);
    }

    /// <summary>
    ///     The helper's removal is in a <c>finally</c>, so the case that proves it is the one where the operation
    ///     around it failed.
    /// </summary>
    [Test]
    public async Task Reset_WhenTheHelperExitsNonZero_FailsWithAStorageErrorAndStillRemovesTheHelper()
    {
        await using var harness = await StoppedHarnessAsync();
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));
        var marker = Path.Combine(row.StoragePath, "volumes", ServiceName, VolumeName, "user-data.txt");
        await File.WriteAllTextAsync(marker, "the wipe never got to this");

        harness.HelperOutcome = static _ => 1;

        _ = await harness.Service.ResetAsync(row.Id, row.Version);
        var failed = await harness.SettleAsync(row.Id, ExternalAppInstanceStatus.Failed);

        AssertEx.Equal(ExternalAppFailureCategory.StorageError, failed.FailureCategory);
        AssertEx.True(File.Exists(marker), "A failed wipe must not be reported over data that is still there.");
        await AssertNoHelperIsLeftAsync(harness, row.Id);
    }

    /// <summary>
    ///     The exit code is the container's claim; the directory is the evidence. A helper that exited 0 having
    ///     deleted nothing is exactly the shape of the defect this whole mechanism replaces.
    /// </summary>
    [Test]
    public async Task Reset_WhenTheHelperClaimsSuccessAndTheDataRemains_FailsWithAStorageError()
    {
        await using var harness = await StoppedHarnessAsync();
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));
        await File.WriteAllTextAsync(Path.Combine(row.StoragePath, "volumes", ServiceName, VolumeName, "user-data.txt"), "still here");

        harness.HelperOutcome = static _ => 0;

        _ = await harness.Service.ResetAsync(row.Id, row.Version);
        var failed = await harness.SettleAsync(row.Id, ExternalAppInstanceStatus.Failed);

        AssertEx.Equal(ExternalAppFailureCategory.StorageError, failed.FailureCategory);
    }

    [Test]
    public async Task Uninstall_RemovesAnApplicationsDataThroughTheHelperAndThenTheDirectory()
    {
        await using var harness = await RunningHarnessAsync();
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));
        await File.WriteAllTextAsync(Path.Combine(row.StoragePath, "volumes", ServiceName, VolumeName, "user-data.txt"), "written by the application");

        var helper = CaptureHelperSpecification(harness);

        _ = await harness.Service.UninstallAsync(row.Id, row.Version);
        await AssertEx.EventuallyAsync(() => !harness.Runner.IsRunning(row.Id),
            TestBudgets.Contended,
            "The uninstall operation never finished.");

        AssertEx.NotNull(helper.Value, "The uninstall removed the directory without ever running the helper.");
        AssertEx.Null(await harness.ReadAsync(row.Id), "The uninstall must delete the row.");
        AssertEx.False(Directory.Exists(row.StoragePath), $"The instance directory '{row.StoragePath}' survived the uninstall.");
        await AssertNoHelperIsLeftAsync(harness, row.Id);
    }

    /// <summary>
    ///     The ordering that the live round bought: the data goes before the row. An uninstall that deleted the row
    ///     over data it could not remove answered a confirmation promising deletion with silence, and left nothing
    ///     behind that could finish the job.
    /// </summary>
    [Test]
    public async Task Uninstall_WhenTheWipeFails_KeepsTheRowAndTheData()
    {
        await using var harness = await RunningHarnessAsync();
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));
        var marker = Path.Combine(row.StoragePath, "volumes", ServiceName, VolumeName, "user-data.txt");
        await File.WriteAllTextAsync(marker, "the wipe never got to this");

        harness.HelperOutcome = static _ => 1;

        _ = await harness.Service.UninstallAsync(row.Id, row.Version);
        var failed = await harness.SettleAsync(row.Id, ExternalAppInstanceStatus.Failed);

        AssertEx.Equal(ExternalAppFailureCategory.StorageError, failed.FailureCategory);
        AssertEx.True(File.Exists(marker), "The application's data must survive an uninstall that could not remove it.");
        AssertEx.NotNull(await harness.ReadAsync(row.Id),
            "The row must survive too: it is the only thing left that a retry can act on.");
    }

    /// <summary>
    ///     The uninstall half of the "a zero exit is not proof" rule. The live teardown showed a helper that exits 0
    ///     having deleted nothing — a capability set that cannot unlink another uid's entries does exactly that — so
    ///     the leftover COUNT, not the exit code, is what an uninstall is allowed to act on.
    /// </summary>
    [Test]
    public async Task Uninstall_WhenTheHelperClaimsSuccessAndTheDataRemains_KeepsTheRowAndTheData()
    {
        await using var harness = await RunningHarnessAsync();
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));
        var marker = Path.Combine(row.StoragePath, "volumes", ServiceName, VolumeName, "user-data.txt");
        await File.WriteAllTextAsync(marker, "a helper that exited 0 without deleting it");

        // Exit 0 and not one entry removed: the shape of the defect the live round found.
        harness.HelperOutcome = static _ => 0;

        _ = await harness.Service.UninstallAsync(row.Id, row.Version);
        var failed = await harness.SettleAsync(row.Id, ExternalAppInstanceStatus.Failed);

        AssertEx.Equal(ExternalAppFailureCategory.StorageError, failed.FailureCategory);
        AssertEx.True(File.Exists(marker), "The data must survive an uninstall whose helper only claimed to remove it.");
        AssertEx.NotNull(await harness.ReadAsync(row.Id),
            "The row must survive too, or nothing is left that a retry could act on.");
    }

    /// <summary>
    ///     An application with no <c>storage[]</c> has no volumes directory, so there is nothing for a helper to do.
    ///     Creating one anyway would pull an image and start a container on every uninstall of every such instance.
    /// </summary>
    [Test]
    public async Task Uninstall_WhenTheApplicationDeclaresNoStorage_RunsNoHelperAtAll()
    {
        await using var harness = await ExternalAppServiceHarness.CreateAsync(ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service(ServiceName)]));
        var row = await InstallAsync(harness);
        var helper = CaptureHelperSpecification(harness);

        _ = await harness.Service.UninstallAsync(row.Id, row.Version);
        await AssertEx.EventuallyAsync(() => !harness.Runner.IsRunning(row.Id),
            TestBudgets.Contended,
            "The uninstall operation never finished.");

        AssertEx.Null(helper.Value, "An instance with no volumes directory must not cost a container and an image pull.");
        AssertEx.False(Directory.Exists(row.StoragePath));
    }

    /// <summary>
    ///     Captures the helper's specification as the runtime is asked to create it. The container is removed in a
    ///     <c>finally</c>, so reading it back from the fake afterwards would find nothing.
    /// </summary>
    private static Captured CaptureHelperSpecification(ExternalAppServiceHarness harness)
    {
        var captured = new Captured();
        harness.Runtime.RunFailure = specification =>
        {
            if (specification.Labels.ContainsKey(ExternalAppLabels.Helper))
            {
                captured.Value = specification;
            }

            return null;
        };

        return captured;
    }

    private static async Task AssertNoHelperIsLeftAsync(ExternalAppServiceHarness harness, Guid instanceId)
    {
        var labels = ExternalAppLabels.ForStorageHelper(harness.Service.InstallId, instanceId);
        AssertEx.Empty(await harness.Runtime.ListContainersDetailedAsync(labels),
            "A storage helper container was left on the runtime.");
    }

    private static ApplicationManifest Manifest()
    {
        return ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service(ServiceName, storage: [new ApplicationStorage(VolumeName, "/var/lib/app")])]);
    }

    private static async Task<ExternalAppInstanceSnapshot> InstallAsync(ExternalAppServiceHarness harness)
    {
        var manifest = await harness.Catalog.GetApplicationAsync("test-app")
                       ?? throw new AssertionException("The harness catalog does not hold the fixture manifest.");

        var admitted = await harness.Service.InstallAsync(new InstallCommand(manifest.Id,
                                        DisplayName: null,
                                        manifest.ManifestVersion,
                                        manifest.ManifestSha256,
                                        new Dictionary<string, string>(StringComparer.Ordinal),
                                        AcceptPermissions: true));

        _ = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Running);
        harness.InstalledId = admitted.Id;

        return AssertEx.NotNull(await harness.ReadAsync(admitted.Id));
    }

    private static async Task<ExternalAppServiceHarness> RunningHarnessAsync()
    {
        var harness = await ExternalAppServiceHarness.CreateAsync(Manifest(),
                                                         static options => options with
                                                         {
                                                             StorageHelperImage = HelperImage
                                                         });
        _ = await InstallAsync(harness);
        return harness;
    }

    private static async Task<ExternalAppServiceHarness> StoppedHarnessAsync()
    {
        var harness = await RunningHarnessAsync();
        var running = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));

        _ = await harness.Service.StopAsync(running.Id, running.Version);
        _ = await harness.SettleAsync(running.Id, ExternalAppInstanceStatus.Stopped);

        return harness;
    }

    /// <summary>A single captured value. A local cannot be assigned from the lambda the fake invokes.</summary>
    private sealed class Captured
    {
        public ContainerSpecification? Value { get; set; }
    }
}
