namespace XE_Local_AI_Engine.Tests.ExternalApps;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The poll that keeps a running instance honest between boots. Reconciliation runs at startup and on an explicit
///     refresh only, and every instance read is served from the database — so without this, a container someone
///     stopped from a terminal leaves the interface reporting the application as running indefinitely.
/// </summary>
public sealed class ExternalAppStateObserverTests
{
    [Test]
    public async Task Observer_WhenAContainerVanished_TransitionsToStoppedUnexpectedlyAndPublishes()
    {
        await using var harness = await RunningHarnessAsync(TwoServiceManifest()).ConfigureAwait(false);
        using var observer = harness.CreateObserver();

        await harness.Runtime.RemoveContainerAsync(harness.Runtime.CreatedContainerIds[1]).ConfigureAwait(false);
        var publishedBefore = harness.Publisher.Events.Count;

        await observer.PollOnceAsync(CancellationToken.None).ConfigureAwait(false);

        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));
        AssertEx.Equal(ExternalAppInstanceStatus.StoppedUnexpectedly, row.Status);
        AssertEx.Equal(ExternalAppFailureCategory.StoppedUnexpectedly, row.FailureCategory);
        AssertEx.Equal(ExternalAppDesiredState.Running, row.DesiredState, "The user still wants this running; only the observation changed.");
        AssertEx.Contains(harness.Publisher.Events.Skip(publishedBefore).Select(static published => published.Kind),
            ExternalAppInstanceEventKind.StoppedUnexpectedly);
    }

    /// <summary>
    ///     The <c>docker stop</c> case, and the reason the detailed listing exists: a stopped container is still
    ///     LISTED, so a poll built on the id-only list would see the same ids as before and report nothing.
    /// </summary>
    [Test]
    public async Task Observer_WhenAContainerIsExitedButStillListed_TransitionsWithinOneTick()
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest()).ConfigureAwait(false);
        using var observer = harness.CreateObserver();

        harness.Runtime.ExitState = static _ => Exited(exitCode: 137);

        await observer.PollOnceAsync(CancellationToken.None).ConfigureAwait(false);

        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));
        AssertEx.Equal(ExternalAppInstanceStatus.StoppedUnexpectedly, row.Status);

        // The whole sentence, not just the number: the state word and the exit code come from two different members
        // of the listing, and a summary that said "is exited" with the code dropped would still contain "137"
        // through nothing but the service name's neighbours.
        AssertEx.Contains(AssertEx.NotNull(row.FailureSummary), "exited with exit code 137");
    }

    /// <summary>
    ///     The budget, asserted as a number. One label-filtered listing per tick is what makes a fifteen-second poll
    ///     cheap enough to run for the life of the node, and the mutation count is the rest of the contract: an
    ///     observer that started, created or removed anything would be a second, unsupervised reconciler.
    /// </summary>
    [Test]
    public async Task Observer_OneTick_MakesExactlyOneListCallAndMutatesNothing()
    {
        await using var harness = await RunningHarnessAsync(TwoServiceManifest()).ConfigureAwait(false);
        using var observer = harness.CreateObserver();

        var listsBefore = harness.Gated.ListDetailedCalls;
        var mutationsBefore = harness.Gated.MutationCalls;

        await observer.PollOnceAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(listsBefore + 1, harness.Gated.ListDetailedCalls);
        AssertEx.Equal(mutationsBefore, harness.Gated.MutationCalls);
        AssertEx.Equal(ExternalAppInstanceStatus.Running, AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A lifecycle operation finishing in the same instant must never be overwritten: the operation saw the
    ///     instance more recently than this tick's listing did, and its own final write is the newer verdict.
    /// </summary>
    [Test]
    public async Task Observer_WhenTheInstanceGateIsHeld_LeavesTheRowAlone()
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest()).ConfigureAwait(false);
        using var observer = harness.CreateObserver();

        await harness.Runtime.RemoveContainerAsync(harness.Runtime.CreatedContainerIds[0]).ConfigureAwait(false);
        using var held = AssertEx.NotNull(await harness.Gate.TryEnterAsync(ExternalAppInstanceGate.InstanceKey(harness.InstalledId)).ConfigureAwait(false));

        await observer.PollOnceAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(ExternalAppInstanceStatus.Running, AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false)).Status);
    }

    [Test]
    public async Task Observer_WhenTheFeatureIsDisabled_DoesNothing()
    {
        var manifest = SingleServiceManifest();
        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest, static options => options with { Enabled = false }).ConfigureAwait(false);
        using var observer = harness.CreateObserver();

        await observer.PollOnceAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 0, harness.Gated.ListDetailedCalls, "A disabled feature must not reach the daemon at all.");
    }

    /// <summary>
    ///     "There is no container runtime right now" is not evidence that an application stopped. Writing that
    ///     verdict from an absent observation would put every running instance into a state only a Start clears.
    /// </summary>
    [Test]
    public async Task Observer_WhenTheRuntimeIsNotReady_WritesNothing()
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest()).ConfigureAwait(false);
        using var observer = harness.CreateObserver();
        var before = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        harness.Resolver.Resolution = FakeContainerRuntimeResolver.UnavailableResolution("The daemon stopped answering.");

        await observer.PollOnceAsync(CancellationToken.None).ConfigureAwait(false);

        var row = AssertEx.NotNull(await harness.ReadAsync(before.Id).ConfigureAwait(false));
        AssertEx.Equal(ExternalAppInstanceStatus.Running, row.Status);
        AssertEx.Equal(before.Version, row.Version);
    }

    /// <summary>
    ///     An instance nobody is watching costs nothing. A tick with no running instance must not open a connection
    ///     to the daemon four times a minute for the life of the node.
    /// </summary>
    [Test]
    public async Task Observer_WithNoRunningInstance_NeverTouchesTheDaemon()
    {
        await using var harness = await StoppedHarnessAsync(SingleServiceManifest()).ConfigureAwait(false);
        using var observer = harness.CreateObserver();
        var listsBefore = harness.Gated.ListDetailedCalls;

        await observer.PollOnceAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(listsBefore, harness.Gated.ListDetailedCalls);
    }

    /// <summary>
    ///     The cadence itself, driven off a clock the test owns rather than a real fifteen seconds. It proves the
    ///     hosted loop is wired to the configured interval — the one thing calling the tick directly cannot show.
    /// </summary>
    [Test]
    public async Task Observer_AsAHostedService_PollsOnceEveryConfiguredInterval()
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest()).ConfigureAwait(false);
        using var observer = harness.CreateObserver();

        await observer.StartAsync(CancellationToken.None).ConfigureAwait(false);

        // Advancing before the timer is armed moves the clock past a window nothing was waiting on.
        await AssertEx.EventuallyAsync(() => harness.Time.ArmedTimerCount > 0, TestBudgets.Contended, "The observer never armed its interval timer.")
                      .ConfigureAwait(false);

        await harness.Runtime.RemoveContainerAsync(harness.Runtime.CreatedContainerIds[0]).ConfigureAwait(false);
        harness.Time.Advance(TimeSpan.FromSeconds(15));

        await AssertEx.EventuallyAsync(
                          () => harness.ReadAsync(harness.InstalledId).GetAwaiter().GetResult() is
                              { Status: ExternalAppInstanceStatus.StoppedUnexpectedly },
                          TestBudgets.Contended,
                          "One interval elapsed and the observer never noticed the missing container.")
                      .ConfigureAwait(false);

        await observer.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    ///     One row, not the tick. A stored snapshot this engine can no longer read would otherwise cost every later
    ///     instance its observation for as long as that row exists, which is forever.
    /// </summary>
    [Test]
    public async Task Observer_WhenOneRowsSnapshotCannotBeRead_StillObservesTheOthers()
    {
        var manifest = SingleServiceManifest();
        await using var harness = await RunningHarnessAsync(manifest).ConfigureAwait(false);
        using var observer = harness.CreateObserver();

        // A second row the observer WILL be able to judge, and which it must reach after the unreadable one.
        var second = await harness.SeedAsync(manifest, ExternalAppInstanceStatus.Running, ExternalAppDesiredState.Running).ConfigureAwait(false);

        // Valid JSON that deserialises to nothing: the shape a snapshot written by a build this engine no longer is.
        await harness.ReplaceManifestSnapshotAsync(harness.InstalledId, "null").ConfigureAwait(false);

        await observer.PollOnceAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(ExternalAppInstanceStatus.StoppedUnexpectedly,
            AssertEx.NotNull(await harness.ReadAsync(second.Id).ConfigureAwait(false)).Status,
            "The tick must carry on past the row it could not judge.");
        AssertEx.Equal(ExternalAppInstanceStatus.Running,
            AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false)).Status,
            "A row nothing could judge keeps the status it had.");
    }

    private static ContainerRunState Exited(long exitCode)
    {
        return new ContainerRunState
        {
            Running = false,
            Status = "exited",
            ExitCode = exitCode,
            OutOfMemoryKilled = false,
            Health = ContainerHealthState.None
        };
    }

    private static ApplicationManifest SingleServiceManifest()
    {
        return ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web", ports: [ExternalAppTestManifests.UiPort(8080)])]);
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

    private static async Task<ExternalAppServiceHarness> RunningHarnessAsync(ApplicationManifest manifest)
    {
        var harness = await ExternalAppServiceHarness.CreateAsync(manifest).ConfigureAwait(false);
        var admitted = await harness.Service.InstallAsync(new InstallCommand(manifest.Id,
                                        DisplayName: null,
                                        manifest.ManifestVersion,
                                        manifest.ManifestSha256,
                                        new Dictionary<string, string>(StringComparer.Ordinal),
                                        AcceptPermissions: true))
                                    .ConfigureAwait(false);

        _ = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Running).ConfigureAwait(false);
        harness.InstalledId = admitted.Id;
        return harness;
    }

    private static async Task<ExternalAppServiceHarness> StoppedHarnessAsync(ApplicationManifest manifest)
    {
        var harness = await RunningHarnessAsync(manifest).ConfigureAwait(false);
        var running = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        _ = await harness.Service.StopAsync(running.Id, running.Version).ConfigureAwait(false);
        _ = await harness.SettleAsync(running.Id, ExternalAppInstanceStatus.Stopped).ConfigureAwait(false);
        return harness;
    }
}
