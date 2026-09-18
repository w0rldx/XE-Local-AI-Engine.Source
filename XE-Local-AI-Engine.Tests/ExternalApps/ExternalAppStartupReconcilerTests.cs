namespace XE_Local_AI_Engine.Tests.ExternalApps;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     What the boot pass does with the three kinds of disagreement it can find: a row an interrupted operation left
///     transient, a settled row whose containers went their own way, and containers no row claims.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ExternalAppStartupReconcilerTests
{
    /// <summary>
    ///     The rule the whole feature's recoverability rests on. Flattening every row to <c>Failed</c> because the
    ///     daemon happens to be down destroys the evidence the next pass needs, and leaves an interrupted uninstall
    ///     unable ever to complete.
    /// </summary>
    [Test]
    public async Task Reconcile_WhenTheRuntimeIsNotReady_WritesNoRowAndSaysSo()
    {
        var manifest = SingleServiceManifest();
        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);
        var seeded = await harness.SeedAsync(manifest, ExternalAppInstanceStatus.Installing);

        harness.Resolver.Resolution = FakeContainerRuntimeResolver.UnavailableResolution("Nothing is listening on the daemon socket.");

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(ExternalAppReconcileSummary.Nothing, summary);

        var row = AssertEx.NotNull(await harness.ReadAsync(seeded.Id));
        AssertEx.Equal(ExternalAppInstanceStatus.Installing, row.Status);
        AssertEx.Equal(seeded.Version, row.Version);
    }

    [Test]
    public async Task Reconcile_WhenTheFeatureIsDisabled_TouchesNothing()
    {
        var manifest = SingleServiceManifest();

        // ONE harness: the row, the reconciler and the assertion have to share a database, or "the row was not
        // touched" is a statement about a database the pass never opened.
        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest, static options => options with
                                                                 {
                                                                     Enabled = false
                                                                 });
        var seeded = await harness.SeedAsync(manifest, ExternalAppInstanceStatus.Installing);

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(ExternalAppReconcileSummary.Nothing, summary);
        AssertEx.Equal(expected: 0, harness.Gated.ListDetailedCalls, "A disabled feature must not reach the daemon at all.");

        var row = AssertEx.NotNull(await harness.ReadAsync(seeded.Id));
        AssertEx.Equal(ExternalAppInstanceStatus.Installing, row.Status);
        AssertEx.Equal(seeded.Version, row.Version, "A pass that judged nothing may not have moved the row's version either.");
    }

    /// <summary>
    ///     Branch A's uninstall arm. Resuming a destructive action the user already authorised is not a new
    ///     destructive decision — and it is the only transient status the pass completes rather than settles.
    /// </summary>
    [Test]
    public async Task BranchA_Uninstalling_CompletesTheUninstallAndRemovesTheRowsAndTheDirectory()
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest());
        var row = await harness.ForceStatusAsync(harness.InstalledId, ExternalAppInstanceStatus.Uninstalling);

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 1, summary.RowsChanged);
        AssertEx.Null(await harness.ReadAsync(row.Id), "An interrupted uninstall must not leave the row behind.");
        AssertEx.NotEmpty(harness.Runtime.RemovedContainerIds);
        AssertEx.False(Directory.Exists(row.StoragePath), $"The instance directory '{row.StoragePath}' survived the completed uninstall.");
        AssertEx.Contains(harness.Publisher.Events.Select(static published => published.Kind), ExternalAppInstanceEventKind.Uninstalled);
    }

    /// <summary>
    ///     The rows go BEFORE the directory, and the order is what this proves: a directory that cannot be removed
    ///     leaves a warning, where a row nothing can act on leaves an instance stuck in the interface for good.
    /// </summary>
    [Test]
    public async Task BranchA_Uninstalling_WhenTheDirectoryCannotBeDeleted_StillRemovesTheRows()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test("The only portable way to make a directory undeletable is Unix permissions on its parent.");
            return;
        }

        await using var harness = await RunningHarnessAsync(SingleServiceManifest());
        var row = await harness.ForceStatusAsync(harness.InstalledId, ExternalAppInstanceStatus.Uninstalling);
        harness.MakeUndeletable(row.StoragePath);

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 1, summary.RowsChanged);
        AssertEx.Null(await harness.ReadAsync(row.Id));
        AssertEx.True(Directory.Exists(row.StoragePath), "The fixture failed to make the directory undeletable, so this proves nothing.");
    }

    /// <summary>
    ///     The row this pass judges is the row as it is NOW, not as the opening list found it. An operation that
    ///     finishes and releases its gate between the two leaves a snapshot that is already history, and every verdict
    ///     here is a daemon mutation — no lost compare-and-swap afterwards could put the containers back.
    /// </summary>
    [Test]
    public async Task Reconcile_WhenAnUpdateFinishesBetweenTheListAndTheJudgement_LeavesTheNewContainersAlone()
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest());
        var stale = await harness.ForceStatusAsync(harness.InstalledId, ExternalAppInstanceStatus.Updating);

        // The rows are read before the containers are listed, so this fires with the stale 'Updating' snapshot
        // already in the pass's hand — which is exactly the interleaving the gate alone cannot exclude.
        var moved = 0;
        harness.Gated.OnListDetailed = () =>
        {
            if (Interlocked.Exchange(ref moved, value: 1) != 0)
            {
                return;
            }

            _ = harness.ForceStatusAsync(stale.Id, ExternalAppInstanceStatus.Running, ExternalAppDesiredState.Running).GetAwaiter().GetResult();
        };

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 1, moved, "The fixture never moved the row, so this proves nothing about a stale snapshot.");
        AssertEx.Equal(expected: 1, summary.RowsSkippedBusy, "A row that moved under the pass is skipped, not judged on what it used to say.");
        AssertEx.Equal(expected: 0, summary.RowsChanged);
        AssertEx.Empty(harness.Runtime.RemovedContainerIds, "The containers the finished update left running must survive the stale verdict.");

        var row = AssertEx.NotNull(await harness.ReadAsync(stale.Id));
        AssertEx.Equal(ExternalAppInstanceStatus.Running, row.Status);
    }

    /// <summary>
    ///     Completing an interrupted uninstall is the one branch that DESTROYS state, so the teardown is a
    ///     precondition rather than a best-effort call: a row deleted while a container survives leaves the container
    ///     running with nothing left that describes it.
    /// </summary>
    [Test]
    public async Task BranchA_WhenAContainerSurvivesTheTeardown_KeepsTheRowForTheNextPassToRetry()
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest());
        var row = await harness.ForceStatusAsync(harness.InstalledId, ExternalAppInstanceStatus.Uninstalling);

        harness.Gated.RemoveFailure = static _ => new DockerRuntimeException("The daemon refused to remove the container.");

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 0, summary.RowsChanged);
        var kept = AssertEx.NotNull(await harness.ReadAsync(row.Id), "The row must survive a teardown that did not complete.");
        AssertEx.Equal(ExternalAppInstanceStatus.Uninstalling, kept.Status, "The status is what makes the next pass run this same branch again.");
        AssertEx.True(Directory.Exists(kept.StoragePath), "A teardown that did not complete must not have deleted the instance's data.");

        // The retry, in the same test: with the daemon healthy the next pass finishes the uninstall it refused to.
        harness.Gated.RemoveFailure = null;
        var second = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 1, second.RowsChanged);
        AssertEx.Null(await harness.ReadAsync(row.Id));
        AssertEx.False(Directory.Exists(kept.StoragePath), "The completed uninstall removes the directory it kept.");
    }

    /// <summary>
    ///     Settle, never resume — and the storage is what makes that safe. An interrupted install keeps everything it
    ///     wrote, so the user's next action is an ordinary reset or uninstall rather than a lost instance.
    /// </summary>
    [Test]
    [Arguments(ExternalAppInstanceStatus.Installing, "being installed")]
    [Arguments(ExternalAppInstanceStatus.Starting, "starting")]
    [Arguments(ExternalAppInstanceStatus.Stopping, "stopping")]
    [Arguments(ExternalAppInstanceStatus.Updating, "being updated")]
    [Arguments(ExternalAppInstanceStatus.Resetting, "being reset")]
    public async Task BranchA_EachOtherTransientStatus_SettlesToFailedNamingTheInterruptedOperation(ExternalAppInstanceStatus status, string verb)
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest());
        var row = await harness.ForceStatusAsync(harness.InstalledId, status);

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 1, summary.RowsChanged);

        var settled = AssertEx.NotNull(await harness.ReadAsync(row.Id));
        AssertEx.Equal(ExternalAppInstanceStatus.Failed, settled.Status);
        AssertEx.Equal(ExternalAppFailureCategory.Unknown, settled.FailureCategory);
        AssertEx.Contains(AssertEx.NotNull(settled.FailureSummary), verb);

        // The containers go and the STORAGE stays: a reset or a start has to be able to recover forward from here.
        AssertEx.NotEmpty(harness.Runtime.RemovedContainerIds);
        AssertEx.True(Directory.Exists(settled.StoragePath), "Settling an interrupted operation must never delete the instance's data.");
    }

    /// <summary>
    ///     The D9 rule, stated as a test: the verdict keys on the DESIRED state and the daemon, never on the status
    ///     the row was left with. A crash after the containers started but before the final write is exactly this.
    /// </summary>
    [Test]
    public async Task BranchB_WhenEveryContainerIsRunningAndVerifies_RestoresAFailedRowToRunning()
    {
        await using var harness = await RunningHarnessAsync(TwoServiceManifest());
        var row = await harness.ForceStatusAsync(harness.InstalledId, ExternalAppInstanceStatus.Failed);

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 1, summary.RowsChanged);

        var restored = AssertEx.NotNull(await harness.ReadAsync(row.Id));
        AssertEx.Equal(ExternalAppInstanceStatus.Running, restored.Status);
        AssertEx.Null(restored.FailureCategory, "Adoption must clear the stale failure, not inherit it.");
        AssertEx.Contains(harness.Publisher.Events.Select(static published => published.Kind), ExternalAppInstanceEventKind.RestoredOnBoot);
        AssertEx.Empty(harness.Runtime.RemovedContainerIds, "Adopting a healthy instance must not remove anything.");
    }

    [Test]
    public async Task BranchB_WhenAContainerIsMissing_ReportsStoppedUnexpectedly()
    {
        await using var harness = await RunningHarnessAsync(TwoServiceManifest());
        await harness.Runtime.RemoveContainerAsync(harness.Runtime.CreatedContainerIds[1]);

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 1, summary.RowsChanged);

        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));
        AssertEx.Equal(ExternalAppInstanceStatus.StoppedUnexpectedly, row.Status);
        AssertEx.Equal(ExternalAppFailureCategory.StoppedUnexpectedly, row.FailureCategory);

        // The desired state is untouched, which is what makes the user's next Start a rebuild rather than a refusal.
        AssertEx.Equal(ExternalAppDesiredState.Running, row.DesiredState);
    }

    /// <summary>
    ///     The case an id-only listing cannot see. <c>docker stop</c> leaves the container LISTED, so without the
    ///     detailed list this instance would be adopted as running and the interface would stay green forever.
    /// </summary>
    [Test]
    public async Task BranchB_WhenAContainerIsExitedButStillListed_ReportsStoppedUnexpectedlyWithItsExitCode()
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest());
        // The single service's only container, killed out of band. It is still LISTED, which is the whole point.
        harness.Runtime.ExitState = static _ => Exited(exitCode: 137);

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 1, summary.RowsChanged);

        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));
        AssertEx.Equal(ExternalAppInstanceStatus.StoppedUnexpectedly, row.Status);

        // The whole sentence: the state word and the exit code come from two different members of the listing, and
        // only one of them going missing would still leave "137" somewhere in the summary.
        AssertEx.Contains(AssertEx.NotNull(row.FailureSummary), "exited with exit code 137");
    }

    /// <summary>
    ///     Reattachment verifies before it trusts. A container that is running and no longer compliant is a POLICY
    ///     failure, not "stopped unexpectedly": it is up, it is serving, and it is not what this instance installed.
    /// </summary>
    [Test]
    public async Task BranchB_WhenAnExistingContainersDigestDiffers_ReportsFailedWithPolicyViolation()
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest());
        harness.Runtime.InspectionMutator = inspection => inspection with
        {
            Image = ExternalAppTestManifests.SecondImage
        };

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 1, summary.RowsChanged);

        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));
        AssertEx.Equal(ExternalAppInstanceStatus.Failed, row.Status);
        AssertEx.Equal(ExternalAppFailureCategory.PolicyViolation, row.FailureCategory);
        AssertEx.Equal(ExternalAppDesiredState.Running, row.DesiredState, "A drifted container is repaired by a Start, so the desired state must survive.");
    }

    /// <summary>
    ///     The image comparison is exact. The runtime client maps the inspection's <c>Config.Image</c> — the
    ///     digest-pinned reference — so an id, or an empty string from a daemon that told us nothing, is a container
    ///     whose provenance cannot be established, and adopting it would serve a container nobody verified.
    /// </summary>
    [Test]
    [Arguments("sha256:9db7b59979c38555a39def84a31fb98b5296952f9e3afd4f6f11f05b07adfab0")]
    [Arguments("")]
    public async Task BranchB_WhenTheObservedImageIsNotTheInstalledReference_RefusesAdoption(string observed)
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest());
        _ = await harness.ForceStatusAsync(harness.InstalledId, ExternalAppInstanceStatus.Failed);

        harness.Runtime.InspectionMutator = inspection => inspection with
        {
            Image = observed
        };

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 1, summary.RowsChanged);

        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));
        AssertEx.Equal(ExternalAppInstanceStatus.Failed, row.Status);
        AssertEx.Equal(ExternalAppFailureCategory.PolicyViolation, row.FailureCategory);
        AssertEx.Contains(AssertEx.NotNull(row.FailureSummary), "an image this instance did not install");
    }

    /// <summary>
    ///     ONE violation per case. Two at once would keep the test green after either rule was deleted, and the
    ///     off-loopback binding is the rule the whole feature exists to refuse.
    /// </summary>
    [Test]
    public async Task BranchB_WhenTheObservedBindingIsOffLoopback_ReportsFailedWithPolicyViolation()
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest());

        harness.Runtime.InspectionMutator = inspection => inspection with
        {
            PublishedPorts =
            [
                .. inspection.PublishedPorts.Select(static port => port with
                {
                    HostIp = "0.0.0.0"
                })
            ]
        };

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 1, summary.RowsChanged);

        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));
        AssertEx.Equal(ExternalAppInstanceStatus.Failed, row.Status);
        AssertEx.Equal(ExternalAppFailureCategory.PolicyViolation, row.FailureCategory);
        var failureSummary = AssertEx.NotNull(row.FailureSummary);
        AssertEx.Contains(failureSummary, "1 policy check");

        // Counted, never quoted: a sibling violation names a host path and this column is rendered in a browser.
        AssertEx.False(failureSummary.Contains("0.0.0.0", StringComparison.Ordinal), "The stored summary quotes no violation.");
    }

    [Test]
    public async Task BranchB_WhenTheObservedContainerIsPrivileged_ReportsFailedWithPolicyViolation()
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest());

        harness.Runtime.InspectionMutator = static inspection => inspection with
        {
            Privileged = true
        };

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 1, summary.RowsChanged);

        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));
        AssertEx.Equal(ExternalAppInstanceStatus.Failed, row.Status);
        AssertEx.Equal(ExternalAppFailureCategory.PolicyViolation, row.FailureCategory);
    }

    [Test]
    public async Task BranchB_WhenTheDesiredStateIsStoppedAndAContainerIsRunning_StopsItAndReportsStopped()
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest());

        // Desired Stopped while the containers are up: what a daemon restart with unless-stopped produces after the
        // user stopped the instance from a build that did not yet write the desired state.
        var row = await harness.ForceStatusAsync(harness.InstalledId, ExternalAppInstanceStatus.Running, ExternalAppDesiredState.Stopped);

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 1, summary.RowsChanged);
        AssertEx.Equal(ExternalAppInstanceStatus.Stopped, AssertEx.NotNull(await harness.ReadAsync(row.Id)).Status);
        AssertEx.NotEmpty(harness.Runtime.StoppedGracePeriods);
    }

    [Test]
    public async Task BranchB_WhenTheDesiredStateIsStoppedAndNothingIsRunning_WritesNothing()
    {
        await using var harness = await StoppedHarnessAsync(SingleServiceManifest());
        var before = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 0, summary.RowsChanged);
        AssertEx.Equal(before.Version, AssertEx.NotNull(await harness.ReadAsync(before.Id)).Version);
    }

    /// <summary>Branch C: containers wearing THIS install id that no row claims are the only ones removed.</summary>
    [Test]
    public async Task Reconcile_RemovesTheContainersOfAnInstanceNoRowClaims()
    {
        await using var harness = await RunningHarnessAsync(TwoServiceManifest());
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));

        // The row goes, the containers stay: an uninstall that died between the two writes, or a database restored
        // from an older backup.
        await harness.DeleteRowAsync(row.Id, row.Version);

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 2, summary.OrphansRemoved);
        AssertEx.Equal(expected: 0, summary.RowsInspected);
        AssertEx.Equal(expected: 2, harness.Runtime.RemovedContainerIds.Count);
        AssertEx.NotEmpty(harness.Runtime.RemovedNetworks);
    }

    /// <summary>
    ///     R2-16. "No row claims it" is decided against the list the pass opened with, and an install admitted a
    ///     moment later is not in it — its row exists, its gate is held and its containers are being created. Branch
    ///     C used to read that as an orphan and remove the containers out from under the live install.
    /// </summary>
    [Test]
    public async Task Reconcile_WhenAnInstallIsInFlightOnAnUnclaimedInstance_KeepsItsContainers()
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest());
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));

        // The containers with no row the pass can see: exactly what an install that inserted its row after the
        // opening ListAsync looks like from here.
        await harness.DeleteRowAsync(row.Id, row.Version);

        var reconciler = harness.CreateReconciler();
        ExternalAppReconcileSummary guarded;
        using (AssertEx.NotNull(await harness.Gate.TryEnterAsync(ExternalAppInstanceGate.InstanceKey(row.Id))))
        {
            guarded = await reconciler.ReconcileAsync();
        }

        AssertEx.Equal(expected: 0, guarded.OrphansRemoved, "An instance somebody holds the gate on is not an orphan.");
        AssertEx.Empty(harness.Runtime.RemovedContainerIds, "The in-flight install's containers must survive the pass.");

        // The negative control, in the same test: once the gate is free the very same containers ARE removed, which
        // is what proves the fixture built a genuine orphan rather than an unreachable one.
        var unguarded = await reconciler.ReconcileAsync();

        AssertEx.Equal(expected: 1, unguarded.OrphansRemoved);
        AssertEx.NotEmpty(harness.Runtime.RemovedContainerIds);
    }

    /// <summary>
    ///     One row's judgement, not the pass. A container removed between the list and the inspect would otherwise
    ///     abort the loop, skip every later row and the orphan sweep, and reach the operator's refresh as a 500.
    /// </summary>
    [Test]
    public async Task Reconcile_WhenOneRowsInspectThrows_CountsItAndStillJudgesTheOthers()
    {
        var manifest = SingleServiceManifest();
        await using var harness = await RunningHarnessAsync(manifest);

        // A second row with no containers at all, so its verdict needs no inspect and is decided from the list.
        var second = await harness.SeedAsync(manifest, ExternalAppInstanceStatus.Running, ExternalAppDesiredState.Running);

        harness.Gated.InspectFailure = static _ =>
            new DockerRuntimeException(DockerDaemonPreflightStatus.ProbeFailed, "No such container: it was removed between the list and the inspect.");

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 1, summary.RowsFailed);
        AssertEx.Equal(expected: 1, summary.RowsChanged, "The row whose judgement needed no inspect must still have been judged.");
        AssertEx.Equal(ExternalAppInstanceStatus.StoppedUnexpectedly, AssertEx.NotNull(await harness.ReadAsync(second.Id)).Status);
        AssertEx.Equal(ExternalAppInstanceStatus.Running,
            AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId)).Status,
            "A row nothing could judge keeps the status it had.");
    }

    /// <summary>
    ///     A stored snapshot the policy now refuses — an image that lost its digest pin — is an instance this engine
    ///     can no longer describe, which is the unverifiable verdict a Start repairs. It is not a reason for every
    ///     later row to lose its own.
    /// </summary>
    [Test]
    public async Task Reconcile_WhenAStoredSnapshotIsRefusedByThePolicy_FailsThatRowAloneAndJudgesTheOthers()
    {
        var manifest = SingleServiceManifest();
        await using var harness = await RunningHarnessAsync(manifest);
        var second = await harness.SeedAsync(manifest, ExternalAppInstanceStatus.Running, ExternalAppDesiredState.Running);

        // Same services and names, so the containers are still found; only the image lost its digest pin, which is
        // what ApplicationContainerPolicy refuses when the plan is rebuilt for verification.
        await harness.ReplaceManifestSnapshotAsync(harness.InstalledId,
                         ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web", ports: [ExternalAppTestManifests.UiPort(8080)], image: "ghcr.io/example/app:1.0.0")]));

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 0, summary.RowsFailed, "A refusal the planner handles is a verdict, not an unjudgeable row.");
        AssertEx.Equal(expected: 2, summary.RowsChanged);

        var refused = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));
        AssertEx.Equal(ExternalAppInstanceStatus.Failed, refused.Status);
        AssertEx.Equal(ExternalAppFailureCategory.Unknown, refused.FailureCategory);
        AssertEx.Contains(AssertEx.NotNull(refused.FailureSummary), "could not be verified");
        AssertEx.Equal(ExternalAppInstanceStatus.StoppedUnexpectedly, AssertEx.NotNull(await harness.ReadAsync(second.Id)).Status);
    }

    /// <summary>
    ///     The one unplannable cause the operator can act on, and the one a Start does NOT repair: the stored snapshot
    ///     reads the container bridge and this node opened none. The generic verdict tells them to start it again —
    ///     the very command <c>ExternalAppService</c>'s Start/Restart admission refuses with
    ///     <c>ExternalAppBlockedReason.BridgeUnavailable</c> — so this branch has to say what admission says, in
    ///     admission's own words.
    /// </summary>
    [Test]
    public async Task BranchB_WhenTheSnapshotNeedsABridgeThisNodeDidNotOpen_NamesTheBridgeInsteadOfAskingForAStart()
    {
        await using var harness = await RunningHarnessAsync(SingleServiceManifest(), withBridge: false);

        // The snapshot is REPLACED rather than installed: admission refuses to install a manifest that reads the
        // bridge on a node without one, so the only way a row reaches this shape is the way a real node reaches it —
        // a stored snapshot that came to need a bridge which is not there. Same service and UI port, so the
        // containers are still found and the verification plan gets as far as the unresolvable built-in.
        await harness.ReplaceManifestSnapshotAsync(harness.InstalledId, BridgeReadingManifest());

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 1, summary.RowsChanged);

        var failed = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));
        AssertEx.Equal(ExternalAppInstanceStatus.Failed, failed.Status);
        AssertEx.Equal(ExternalAppFailureCategory.ConfigurationMissing, failed.FailureCategory, "A bridge this node never opened is a configuration answer, not an unidentified one.");
        AssertEx.Equal(ExternalAppService.BridgeUnavailableDetail,
            AssertEx.NotNull(failed.FailureSummary),
            "The boot verdict and the Start refusal hand the operator ONE wording, or the two drift apart without a gate noticing.");
    }

    /// <summary>
    ///     The install-id filter is the security property. A container wearing this feature's owner label under a
    ///     different install id belongs to another installation of this engine — counted so it is visible, never
    ///     removed, because removing it would be this node deleting somebody else's running application.
    /// </summary>
    [Test]
    public async Task Reconcile_WithOwnerLabelledContainersWhoseInstallLabelIsMissingOrDifferent_CountsThemAndRemovesNothing()
    {
        await using var harness = await ExternalAppServiceHarness.CreateAsync(SingleServiceManifest());

        var differentInstall = await RunForeignAsync(harness, "stranger", installId: "a-different-installation");
        var noInstall = await RunForeignAsync(harness, "unlabelled", installId: null);

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 2, summary.ForeignInstallContainers);
        AssertEx.Equal(expected: 0, summary.OrphansRemoved);
        AssertEx.Empty(harness.Runtime.RemovedContainerIds,
            $"Containers '{differentInstall}' and '{noInstall}' belong to another installation and must never be removed from here.");
    }

    /// <summary>
    ///     A refresh landing on an instance an update is running would remove containers under the live updater, and
    ///     no lost compare-and-swap can undo a daemon mutation. The gate is the answer, and skipping is counted.
    /// </summary>
    [Test]
    public async Task Reconcile_SkipsAnInstanceWhoseGateIsHeld_AndCountsIt()
    {
        var manifest = SingleServiceManifest();
        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);
        var seeded = await harness.SeedAsync(manifest, ExternalAppInstanceStatus.Updating);

        using var held = AssertEx.NotNull(await harness.Gate.TryEnterAsync(ExternalAppInstanceGate.InstanceKey(seeded.Id)));

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 1, summary.RowsSkippedBusy);
        AssertEx.Equal(expected: 0, summary.RowsChanged);
        AssertEx.Equal(ExternalAppInstanceStatus.Updating, AssertEx.NotNull(await harness.ReadAsync(seeded.Id)).Status);
    }

    /// <summary>
    ///     The refresh endpoint re-runs this pass, so a second run must reach the same state rather than undoing the
    ///     first one's verdict or tearing down what it just adopted.
    /// </summary>
    [Test]
    public async Task ReconcileAsync_CalledTwice_IsIdempotent()
    {
        await using var harness = await RunningHarnessAsync(TwoServiceManifest());
        _ = await harness.ForceStatusAsync(harness.InstalledId, ExternalAppInstanceStatus.Failed);
        var reconciler = harness.CreateReconciler();

        var first = await reconciler.ReconcileAsync();
        var afterFirst = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));
        var second = await reconciler.ReconcileAsync();
        var afterSecond = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));

        AssertEx.Equal(ExternalAppInstanceStatus.Running, afterFirst.Status);
        AssertEx.Equal(ExternalAppInstanceStatus.Running, afterSecond.Status);
        AssertEx.Equal(first.RowsInspected, second.RowsInspected);
        AssertEx.Equal(expected: 0, second.OrphansRemoved);
        AssertEx.Empty(harness.Runtime.RemovedContainerIds, "A second pass must not tear down the instance the first one adopted.");
    }

    /// <summary>
    ///     The other half of the shutdown contract, and the reason a cancelled-by-shutdown pipeline leaves its row
    ///     transient rather than settling it: this pass is what settles it, on the next boot, with the containers it
    ///     never touched still where the daemon left them.
    /// </summary>
    [Test]
    public async Task Reconcile_AfterAShutdownLeftAnOperationTransient_SettlesTheRowAndTouchesNothingElse()
    {
        var manifest = SingleServiceManifest();
        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);
        harness.Gated.PullGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var admitted = await harness.Service.InstallAsync(new InstallCommand(manifest.Id,
                                        DisplayName: null,
                                        manifest.ManifestVersion,
                                        manifest.ManifestSha256,
                                        new Dictionary<string, string>(StringComparer.Ordinal),
                                        AcceptPermissions: true));
        await harness.Gated.PullReached.Task.WaitAsync(TestBudgets.Contended);

        harness.StopHost();
        await harness.WaitUntilIdleAsync(admitted.Id);
        AssertEx.Equal(ExternalAppInstanceStatus.Installing,
            AssertEx.NotNull(await harness.ReadAsync(admitted.Id)).Status,
            "The shutdown path must leave the row transient; without that this pass has nothing to settle.");

        var summary = await harness.CreateReconciler().ReconcileAsync();

        AssertEx.Equal(expected: 1, summary.RowsChanged);

        var settled = AssertEx.NotNull(await harness.ReadAsync(admitted.Id));
        AssertEx.Equal(ExternalAppInstanceStatus.Failed, settled.Status);
        AssertEx.Contains(AssertEx.NotNull(settled.FailureSummary), "being installed");
    }

    /// <summary>
    ///     A node whose Docker daemon is broken must still start. An application the user can see and act on is worth
    ///     more than a reconciliation that ran.
    /// </summary>
    [Test]
    public async Task StartAsync_WhenThePassThrows_DoesNotFailStartup()
    {
        var manifest = SingleServiceManifest();
        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);
        var seeded = await harness.SeedAsync(manifest, ExternalAppInstanceStatus.Installing);

        harness.Resolver.CreateFailure = new InvalidOperationException("The daemon socket vanished mid-pass.");

        // Returning at all is the assertion's other half: StartAsync throwing is precisely the failure this guards
        // against, and the platform would report it as this test's own exception.
        await harness.CreateReconciler().StartAsync(CancellationToken.None);

        AssertEx.Equal(ExternalAppInstanceStatus.Installing, AssertEx.NotNull(await harness.ReadAsync(seeded.Id)).Status);
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

    /// <summary>
    ///     A container wearing this feature's owner label but not this installation's id. Built by hand because the
    ///     only other way to produce one is a second engine installation pointed at the same daemon.
    /// </summary>
    /// <summary>
    ///     Another installation's container, created the way the daemon would already hold one: its image pulled and
    ///     its network created before the create, because the fake refuses a create that has neither, as a daemon does.
    /// </summary>
    private static Task<string> RunForeignAsync(ExternalAppServiceHarness harness, string name, string? installId)
    {
        var specification = ForeignSpecification(name, installId);
        harness.Runtime.SeedExistingImage(specification.Image);
        harness.Runtime.SeedExistingNetwork(new ContainerNetworkSpecification
        {
            Name = specification.NetworkName,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["com.example.owner"] = "someone-else"
            },
            Internal = false
        });

        return harness.Runtime.RunContainerAsync(specification);
    }

    private static ContainerSpecification ForeignSpecification(string name, string? installId)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExternalAppLabels.Owner] = ExternalAppLabels.OwnerValue,
            [ExternalAppLabels.Instance] = Guid.NewGuid().ToString("N"),
            [ExternalAppLabels.Service] = "web"
        };

        if (installId is not null)
        {
            labels[ExternalAppLabels.Install] = installId;
        }

        return new ContainerSpecification
        {
            Image = ExternalAppTestManifests.Image,
            Name = "xe-app-foreign-" + name,
            Labels = labels,
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
            Mounts = [],
            PublishedPorts = [],
            CapabilitiesToDrop = ["ALL"],
            CapabilitiesToAdd = [],
            SecurityOptions = ["no-new-privileges:true"],
            ReadOnlyRootFilesystem = false,
            NetworkName = "xe-app-foreign-" + name,
            NetworkAliases = ["web"],
            RestartMode = ContainerRestartMode.UnlessStopped,
            MemoryBytes = 0,
            NanoCpus = 0,
            PidsLimit = 512
        };
    }

    private static ApplicationManifest SingleServiceManifest()
    {
        return ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web", ports: [ExternalAppTestManifests.UiPort(8080)])]);
    }

    /// <summary>
    ///     <see cref="SingleServiceManifest" />'s service and UI port, plus an environment value that substitutes a
    ///     bridge built-in — the one thing <c>DeploymentPlanner.Plan</c> cannot resolve without a grant.
    /// </summary>
    private static ApplicationManifest BridgeReadingManifest()
    {
        return ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("web",
                environment: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["OPENAI_BASE_URL"] = "http://${XE_BRIDGE_ENDPOINT}/llm/v1"
                },
                ports: [ExternalAppTestManifests.UiPort(8080)])
        ]);
    }

    private static ApplicationManifest TwoServiceManifest()
    {
        return ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("web", ports: [ExternalAppTestManifests.UiPort(8080)]),
            ExternalAppTestManifests.Service("sidecar",
                dependsOn: [new ApplicationDependency("web", "started")],
                image: ExternalAppTestManifests.SecondImage)
        ]);
    }

    private static async Task<ExternalAppServiceHarness> RunningHarnessAsync(ApplicationManifest manifest, bool withBridge = true)
    {
        var harness = await ExternalAppServiceHarness.CreateAsync(manifest, withBridge: withBridge);
        var admitted = await harness.Service.InstallAsync(new InstallCommand(manifest.Id,
                                        DisplayName: null,
                                        manifest.ManifestVersion,
                                        manifest.ManifestSha256,
                                        new Dictionary<string, string>(StringComparer.Ordinal),
                                        AcceptPermissions: true));

        _ = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Running);
        harness.InstalledId = admitted.Id;
        return harness;
    }

    private static async Task<ExternalAppServiceHarness> StoppedHarnessAsync(ApplicationManifest manifest)
    {
        var harness = await RunningHarnessAsync(manifest);
        var running = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId));

        _ = await harness.Service.StopAsync(running.Id, running.Version);
        _ = await harness.SettleAsync(running.Id, ExternalAppInstanceStatus.Stopped);
        return harness;
    }
}
