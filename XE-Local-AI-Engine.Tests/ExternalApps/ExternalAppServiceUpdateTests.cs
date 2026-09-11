namespace XE_Local_AI_Engine.Tests.ExternalApps;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The update preview and the update pipeline: what the dialog is told, what admission refuses before anything
///     is stopped, and where the row's single recovery boundary sits.
/// </summary>
public sealed class ExternalAppServiceUpdateTests
{
    [Test]
    public async Task PreviewUpdate_ReportsTheTargetFingerprintItsVariablesAndTheAddedPermissions()
    {
        await using var harness = await InstalledAsync(Version1()).ConfigureAwait(false);
        var target = ExternalAppTestManifests.Manifest(
            [ExternalAppTestManifests.Service("web", capAdd: ["CHOWN"])],
            variables: [ExternalAppTestManifests.Variable("LLM_HOST", @default: "http://localhost:11434")],
            manifestVersion: 2);
        ExternalAppServiceHarness.Seed(harness.Catalog, target);

        var preview = await harness.Service.PreviewUpdateAsync(harness.InstalledId).ConfigureAwait(false);

        AssertEx.True(preview.CanUpdate, "A newer manifest that passes every precondition can be updated to.");
        AssertEx.Null(preview.BlockedReason);
        AssertEx.Equal(expected: 1, preview.CurrentManifestVersion);
        AssertEx.Equal(expected: 2, preview.TargetManifestVersion);
        AssertEx.Equal(target.ManifestSha256, preview.ManifestSha256);
        AssertEx.Contains(preview.AddedPermissions, "capabilities");
        AssertEx.Contains(preview.Variables, variable => string.Equals(variable.Name, "LLM_HOST", StringComparison.Ordinal));
    }

    [Test]
    public async Task PreviewUpdate_WhenTheApplicationLeftTheCatalog_Returns200BlockedWithCatalogMissing()
    {
        await using var harness = await InstalledAsync(Version1()).ConfigureAwait(false);
        ExternalAppServiceHarness.Seed(harness.Catalog);

        var preview = await harness.Service.PreviewUpdateAsync(harness.InstalledId).ConfigureAwait(false);

        AssertEx.False(preview.CanUpdate, "There is no target manifest to update to.");
        AssertEx.Equal(ExternalAppBlockedReason.CatalogMissing, preview.BlockedReason);
        AssertEx.Empty(preview.AddedPermissions);
        AssertEx.Equal(preview.CurrentManifestVersion, preview.TargetManifestVersion);
    }

    [Test]
    public async Task Update_WhenTheApplicationLeftTheCatalog_Is404AndDoesNotStopTheInstance()
    {
        await using var harness = await InstalledAsync(Version1()).ConfigureAwait(false);
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));
        ExternalAppServiceHarness.Seed(harness.Catalog);

        _ = await AssertEx.ThrowsAsync<ExternalAppNotFoundException>(
            () => harness.Service.UpdateAsync(row.Id, row.Version, Command(Version1()))).ConfigureAwait(false);

        var after = AssertEx.NotNull(await harness.ReadAsync(row.Id).ConfigureAwait(false));
        AssertEx.Equal(ExternalAppInstanceStatus.Running, after.Status);
        AssertEx.Equal(row.Version, after.Version);
    }

    [Test]
    public async Task Update_WhenTheCatalogVersionIsNotNewer_ReportsAlreadyCurrentAndDoesNothing()
    {
        await using var harness = await InstalledAsync(Version1()).ConfigureAwait(false);
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        var summary = await harness.Service.UpdateAsync(row.Id, row.Version, Command(Version1())).ConfigureAwait(false);

        AssertEx.Equal(ExternalAppInstanceStatus.Running, summary.Status);
        AssertEx.Equal(row.Version, summary.Version);
    }

    [Test]
    public async Task Update_WithAStaleManifestSha_Is409ManifestChanged()
    {
        await using var harness = await InstalledAsync(Version1()).ConfigureAwait(false);
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));
        var target = Version2();
        ExternalAppServiceHarness.Seed(harness.Catalog, target);

        _ = await AssertEx.ThrowsAsync<ExternalAppManifestChangedException>(
            () => harness.Service.UpdateAsync(row.Id, row.Version, Command(target) with { ManifestSha256 = new string('d', 64) }))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Every install precondition runs against the TARGET before anything is stopped. An installed application
    ///     must not be able to update into a manifest this version would refuse to install.
    /// </summary>
    [Test]
    public async Task Update_WhenTheTargetFailsAPrecondition_IsRefusedBeforeTheInstanceIsStopped()
    {
        await using var harness = await InstalledAsync(Version1()).ConfigureAwait(false);
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        var target = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")],
            permissions: new ApplicationPermissions(Internet: true, LocalNetwork: false, "none", "required"),
            manifestVersion: 2);
        ExternalAppServiceHarness.Seed(harness.Catalog, target);

        var failure = await AssertEx.ThrowsAsync<ExternalAppValidationException>(
            () => harness.Service.UpdateAsync(row.Id, row.Version, Command(target))).ConfigureAwait(false);

        AssertEx.Contains(failure.Message, nameof(ExternalAppBlockedReason.GpuNotSupported));

        var after = AssertEx.NotNull(await harness.ReadAsync(row.Id).ConfigureAwait(false));
        AssertEx.Equal(ExternalAppInstanceStatus.Running, after.Status);
        AssertEx.Empty(harness.Runtime.RemovedContainerIds, "Nothing is torn down for an update that never began.");
    }

    [Test]
    public async Task Update_WithANewlyRequiredVariable_NamesItAndSucceedsWhenTheCommandSuppliesIt()
    {
        await using var harness = await InstalledAsync(Version1()).ConfigureAwait(false);
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        var target = ExternalAppTestManifests.Manifest(
            [ExternalAppTestManifests.Service("web", environment: new Dictionary<string, string>(StringComparer.Ordinal) { ["KEY"] = "${API_KEY}" })],
            variables: [ExternalAppTestManifests.Variable("API_KEY", required: true)],
            manifestVersion: 2);
        ExternalAppServiceHarness.Seed(harness.Catalog, target);

        var failure = await AssertEx.ThrowsAsync<ExternalAppValidationException>(
            () => harness.Service.UpdateAsync(row.Id, row.Version, Command(target))).ConfigureAwait(false);
        AssertEx.Contains(failure.Names, "API_KEY");

        // The Settings tab is driven by the OLD snapshot and is disabled while running, so the command is the only
        // place a newly required value can come from.
        var admitted = await harness.Service.UpdateAsync(row.Id,
                                        row.Version,
                                        Command(target) with
                                        {
                                            Variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["API_KEY"] = "supplied-now" }
                                        })
                                    .ConfigureAwait(false);

        var updated = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Running).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, updated.ManifestVersion);
    }

    [Test]
    public async Task Update_ThatWidensPermissions_NeedsAcknowledgementAndThenRecordsIt()
    {
        await using var harness = await InstalledAsync(Version1()).ConfigureAwait(false);
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        var target = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web", capAdd: ["CHOWN"])], manifestVersion: 2);
        ExternalAppServiceHarness.Seed(harness.Catalog, target);

        var refused = await AssertEx.ThrowsAsync<ExternalAppPermissionChangeRequiresAcknowledgementException>(
            () => harness.Service.UpdateAsync(row.Id, row.Version, Command(target) with { AcceptPermissions = false })).ConfigureAwait(false);
        AssertEx.Contains(refused.AddedPermissions, "capabilities");

        var admitted = await harness.Service.UpdateAsync(row.Id, row.Version, Command(target)).ConfigureAwait(false);
        _ = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Running).ConfigureAwait(false);

        var kinds = (await harness.ReadEventsAsync(row.Id).ConfigureAwait(false)).Select(entry => entry.Kind).ToList();

        // Written before the stop, so the acknowledgement is on the record even if the update then fails.
        AssertEx.True(kinds.IndexOf(ExternalAppInstanceEventKind.PermissionAccepted) < kinds.IndexOf(ExternalAppInstanceEventKind.UpdateRequested),
            "The acknowledgement must be recorded before the update is requested.");
        AssertEx.Contains(kinds, ExternalAppInstanceEventKind.Updated);
    }

    [Test]
    public async Task Update_WhenAVariableTurnsFromSecretToString_DiscardsTheStoredValueAndNeverReturnsIt()
    {
        var installed = ExternalAppTestManifests.Manifest(
            [ExternalAppTestManifests.Service("web", environment: new Dictionary<string, string>(StringComparer.Ordinal) { ["PW"] = "${TOKEN}" })],
            variables: [ExternalAppTestManifests.Variable("TOKEN", required: true, type: "secret")]);

        await using var harness = await InstalledAsync(installed,
                                            new Dictionary<string, string>(StringComparer.Ordinal) { ["TOKEN"] = "the-old-secret" })
                                        .ConfigureAwait(false);
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        var target = ExternalAppTestManifests.Manifest(
            [ExternalAppTestManifests.Service("web", environment: new Dictionary<string, string>(StringComparer.Ordinal) { ["PW"] = "${TOKEN}" })],
            variables: [ExternalAppTestManifests.Variable("TOKEN", required: true)],
            manifestVersion: 2);
        ExternalAppServiceHarness.Seed(harness.Catalog, target);

        var preview = await harness.Service.PreviewUpdateAsync(row.Id).ConfigureAwait(false);
        AssertEx.False(preview.CurrentValues.ContainsKey("TOKEN"),
            "A value that was secret and is now plain is treated as UNSET, never handed back in the clear.");

        // It is required in the target, so the update has to be given one.
        var failure = await AssertEx.ThrowsAsync<ExternalAppValidationException>(
            () => harness.Service.UpdateAsync(row.Id, row.Version, Command(target))).ConfigureAwait(false);
        AssertEx.Contains(failure.Names, "TOKEN");

        var admitted = await harness.Service.UpdateAsync(row.Id,
                                        row.Version,
                                        Command(target) with
                                        {
                                            Variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["TOKEN"] = "a-new-plain-value" }
                                        })
                                    .ConfigureAwait(false);
        _ = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Running).ConfigureAwait(false);

        var after = AssertEx.NotNull(await harness.ReadAsync(row.Id).ConfigureAwait(false));
        AssertEx.False(after.VariablesJson.Contains("the-old-secret", StringComparison.Ordinal),
            "The old secret must not survive the reclassification.");
    }

    /// <summary>
    ///     The commit sits after every replacement container is created and verified and before any is started. It is
    ///     the recovery boundary: before it the row still describes the old version, after it a start recovers forward.
    /// </summary>
    [Test]
    public async Task Update_CommitsAfterEveryContainerIsCreatedAndBeforeAnyIsStarted()
    {
        await using var harness = await InstalledAsync(Version1()).ConfigureAwait(false);
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));
        var target = Version2();
        ExternalAppServiceHarness.Seed(harness.Catalog, target);

        var versionAtCreate = new List<int>();
        harness.Runtime.RunFailure = _ =>
        {
            versionAtCreate.Add(harness.ReadAsync(row.Id).GetAwaiter().GetResult()!.ManifestVersion);
            return null;
        };

        var versionAtStart = new List<int>();
        harness.Gated.OnStart = _ => versionAtStart.Add(harness.ReadAsync(row.Id).GetAwaiter().GetResult()!.ManifestVersion);

        var admitted = await harness.Service.UpdateAsync(row.Id, row.Version, Command(target)).ConfigureAwait(false);
        _ = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Running).ConfigureAwait(false);

        AssertEx.NotEmpty(versionAtCreate);
        AssertEx.NotEmpty(versionAtStart);
        AssertEx.Equal(expected: 1, versionAtCreate[0]);
        AssertEx.Equal(expected: 2, versionAtStart[0]);
    }

    [Test]
    public async Task Update_WhenAVerifyFailsBeforeTheCommit_KeepsTheOldSnapshotAndRemovesTheNewContainers()
    {
        await using var harness = await InstalledAsync(Version1()).ConfigureAwait(false);
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));
        var target = Version2();
        ExternalAppServiceHarness.Seed(harness.Catalog, target);

        harness.Runtime.InspectionMutator = inspection => inspection with { Privileged = true };

        var admitted = await harness.Service.UpdateAsync(row.Id, row.Version, Command(target)).ConfigureAwait(false);
        var after = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Failed).ConfigureAwait(false);

        AssertEx.Equal(ExternalAppFailureCategory.PolicyViolation, after.FailureCategory);
        AssertEx.Equal(expected: 1, after.ManifestVersion);
        AssertEx.NotEmpty(harness.Runtime.RemovedContainerIds);
    }

    [Test]
    public async Task Update_WhenAStartFailsAfterTheCommit_KeepsTheNewSnapshotSoStartRecoversForward()
    {
        await using var harness = await InstalledAsync(Version1()).ConfigureAwait(false);
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));
        var target = Version2();
        ExternalAppServiceHarness.Seed(harness.Catalog, target);

        harness.Gated.StartFailure = _ => new Client.Services.Sandbox.Container.DockerRuntimeException("The daemon refused the start.");

        var admitted = await harness.Service.UpdateAsync(row.Id, row.Version, Command(target)).ConfigureAwait(false);
        var after = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Failed).ConfigureAwait(false);

        // The row describes the TARGET. Rolling back is the unsafe direction once a replacement may already have
        // migrated bind-mounted data, so the next start goes forward onto the new images.
        AssertEx.Equal(expected: 2, after.ManifestVersion);

        harness.Gated.StartFailure = null;
        _ = await harness.Service.StartAsync(after.Id, after.Version).ConfigureAwait(false);
        var recovered = await harness.SettleAsync(after.Id, ExternalAppInstanceStatus.Running).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, recovered.ManifestVersion);
    }

    [Test]
    public async Task Update_WhenTheCommitLosesItsCompareAndSwap_AbortsWithoutStartingAnything()
    {
        await using var harness = await InstalledAsync(Version1()).ConfigureAwait(false);
        var row = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));
        var target = Version2();
        ExternalAppServiceHarness.Seed(harness.Catalog, target);

        // Another writer moves the row on while the replacement container is being created, so the commit's swap
        // is against a version that no longer exists.
        harness.Runtime.RunFailure = _ =>
        {
            harness.BumpVersionAsync(row.Id).GetAwaiter().GetResult();
            return null;
        };

        var startedAnything = false;
        harness.Gated.OnStart = _ => startedAnything = true;

        var admitted = await harness.Service.UpdateAsync(row.Id, row.Version, Command(target)).ConfigureAwait(false);
        var after = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Failed).ConfigureAwait(false);

        AssertEx.False(startedAnything, "An aborted commit must not start a single replacement container.");
        AssertEx.Equal(expected: 1, after.ManifestVersion);
    }

    [Test]
    public async Task Update_OfAStoppedInstance_LeavesItStoppedAndKeepsItsStorage()
    {
        var manifest = ExternalAppTestManifests.Manifest(
            [ExternalAppTestManifests.Service("web", storage: [new ApplicationStorage("data", "/var/lib/app")])]);

        await using var harness = await InstalledAsync(manifest).ConfigureAwait(false);
        var running = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        _ = await harness.Service.StopAsync(running.Id, running.Version).ConfigureAwait(false);
        var stopped = await harness.SettleAsync(running.Id, ExternalAppInstanceStatus.Stopped).ConfigureAwait(false);

        var userData = Path.Combine(stopped.StoragePath, "volumes", "web", "data", "user-data.txt");
        await File.WriteAllTextAsync(userData, "kept across the update").ConfigureAwait(false);

        var target = ExternalAppTestManifests.Manifest(
            [ExternalAppTestManifests.Service("web", storage: [new ApplicationStorage("data", "/var/lib/app")])],
            manifestVersion: 2);
        ExternalAppServiceHarness.Seed(harness.Catalog, target);

        var admitted = await harness.Service.UpdateAsync(stopped.Id, stopped.Version, Command(target)).ConfigureAwait(false);
        var after = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Stopped).ConfigureAwait(false);

        AssertEx.Equal(ExternalAppDesiredState.Stopped, after.DesiredState);
        AssertEx.Equal(expected: 2, after.ManifestVersion);
        AssertEx.True(File.Exists(userData), "An update never touches the instance's volumes.");
    }

    [Test]
    public async Task Detail_CarriesTheSanitisedInstalledSnapshotAndReportsACatalogThatLostIt()
    {
        var manifest = ExternalAppTestManifests.Manifest(
        [
            ExternalAppTestManifests.Service("web", files: [ExternalAppTestManifests.File("settings.yml", "/etc/app/settings.yml", "server: local\n")])
        ],
            variables: [ExternalAppTestManifests.Variable("TOKEN", type: "secret", @default: "a-placeholder-the-catalog-ships")]);

        await using var harness = await InstalledAsync(manifest).ConfigureAwait(false);

        var detail = await harness.Service.GetAsync(harness.InstalledId).ConfigureAwait(false);

        AssertEx.Empty(detail.Manifest.Services[0].Files, "Asset bodies are catalog content, not instance state.");
        AssertEx.Null(detail.Manifest.Variables[0].Default, "A secret's default is nulled on the way out.");
        AssertEx.NotNullOrEmpty(detail.TestedVersion);
        AssertEx.False(detail.Summary.CatalogMissing, "The application is still in the catalog.");

        ExternalAppServiceHarness.Seed(harness.Catalog);
        var afterwards = await harness.Service.GetAsync(harness.InstalledId).ConfigureAwait(false);

        AssertEx.True(afterwards.Summary.CatalogMissing, "An application that left the catalog is reported as missing.");
        AssertEx.False(afterwards.Summary.UpdateAvailable, "There is nothing to update to.");
        AssertEx.Null(afterwards.Summary.AvailableManifestVersion);
    }

    /// <summary>
    ///     The list the Apps page reads. It carries the same sanitised projection <c>GetAsync</c> returns, one per row,
    ///     so a card renders its manifest and its ports without a read per instance.
    /// </summary>
    [Test]
    public async Task ListDetails_ProjectsEveryRowAsTheSameSanitisedDetail()
    {
        var manifest = ExternalAppTestManifests.Manifest(
        [
            ExternalAppTestManifests.Service("web", files: [ExternalAppTestManifests.File("settings.yml", "/etc/app/settings.yml", "server: local\n")])
        ],
            variables: [ExternalAppTestManifests.Variable("TOKEN", type: "secret", @default: "a-placeholder-the-catalog-ships")]);

        await using var harness = await InstalledAsync(manifest).ConfigureAwait(false);

        var details = await harness.Service.ListDetailsAsync().ConfigureAwait(false);

        AssertEx.Equal(expected: 1, details.Count);
        AssertEx.Equal(harness.InstalledId, details[0].Summary.Id);
        AssertEx.Empty(details[0].Manifest.Services[0].Files, "The list sanitises exactly as the single read does.");
        AssertEx.Null(details[0].Manifest.Variables[0].Default, "A secret's default is nulled here too.");
        AssertEx.False(details[0].Summary.CatalogMissing, "The application is still in the catalog.");
    }

    /// <summary>
    ///     A row whose stored manifest snapshot cannot be read must not take the list down with it. The list projects
    ///     every row through one deserialize, so a truncated document or one that reads as null would otherwise cost
    ///     the page every healthy instance AND the uninstall that is the only way to be rid of the bad row.
    /// </summary>
    [Test]
    [Arguments("{")]
    [Arguments("null")]
    public async Task ListDetails_WithARowWhoseManifestSnapshotCannotBeRead_DegradesThatRowAndKeepsTheRest(string manifestJson)
    {
        await using var harness = await InstalledAsync(Version1()).ConfigureAwait(false);
        var corruptId = await harness.CreateRowWithManifestJsonAsync("corrupt-app", "Corrupt App", manifestJson).ConfigureAwait(false);

        var details = await harness.Service.ListDetailsAsync().ConfigureAwait(false);

        AssertEx.Equal(expected: 2, details.Count, "The healthy instance is still listed alongside the unreadable one.");

        var healthy = AssertEx.NotNull(details.SingleOrDefault(detail => detail.Summary.Id == harness.InstalledId));
        AssertEx.NotEmpty(healthy.Manifest.Services, "The healthy row is projected exactly as before.");
        AssertEx.Null(healthy.Summary.FailureSummary);

        var degraded = AssertEx.NotNull(details.SingleOrDefault(detail => detail.Summary.Id == corruptId));
        AssertEx.Empty(degraded.Manifest.Services, "Nothing is claimed about a manifest that could not be read.");
        AssertEx.Empty(degraded.MaskedVariables, "Which variables are secret is a manifest fact; an unclassifiable map is not rendered.");
        AssertEx.Equal("Corrupt App", degraded.Summary.DisplayName, "The row's own members still render, so the card can be acted on.");
        AssertEx.Equal(ExternalAppFailureCategory.Unknown, degraded.Summary.FailureCategory);
        var marker = AssertEx.NotNull(degraded.Summary.FailureSummary);
        AssertEx.Contains(marker, "could not be read");

        // The marker says only that: no JSON fragment, no path, nothing from the stored document.
        AssertEx.False(marker.Contains(manifestJson, StringComparison.Ordinal), "The marker must not quote the document it could not read.");
    }

    /// <summary>The single read degrades the same way, so the detail page of an unreadable row opens instead of 500ing.</summary>
    [Test]
    public async Task Get_WithARowWhoseManifestSnapshotCannotBeRead_DegradesRatherThanThrowing()
    {
        await using var harness = await InstalledAsync(Version1()).ConfigureAwait(false);
        var corruptId = await harness.CreateRowWithManifestJsonAsync("corrupt-app", "Corrupt App", "{").ConfigureAwait(false);

        var detail = await harness.Service.GetAsync(corruptId).ConfigureAwait(false);

        AssertEx.Equal(corruptId, detail.Summary.Id);
        AssertEx.Empty(detail.Manifest.Services);
        AssertEx.Contains(AssertEx.NotNull(detail.Summary.FailureSummary), "could not be read");
    }

    /// <summary>
    ///     The restore-to-stopped at the end of an update stops the containers the update just CREATED, so the target
    ///     manifest is what names them. Against the installed snapshot, a service the target added stays running under
    ///     its unless-stopped policy while the row says the application is stopped.
    /// </summary>
    [Test]
    public async Task Update_WhenTheTargetAddsAServiceAndTheInstanceWasStopped_StopsTheAddedServiceToo()
    {
        await using var harness = await InstalledAsync(Version1()).ConfigureAwait(false);
        var running = AssertEx.NotNull(await harness.ReadAsync(harness.InstalledId).ConfigureAwait(false));

        _ = await harness.Service.StopAsync(running.Id, running.Version).ConfigureAwait(false);
        var stopped = await harness.SettleAsync(running.Id, ExternalAppInstanceStatus.Stopped).ConfigureAwait(false);

        var target = ExternalAppTestManifests.Manifest(
        [
            ExternalAppTestManifests.Service("web"),
            ExternalAppTestManifests.Service("sidecar",
                dependsOn: [new ApplicationDependency("web", "started")],
                image: ExternalAppTestManifests.SecondImage)
        ],
            manifestVersion: 2);
        ExternalAppServiceHarness.Seed(harness.Catalog, target);

        var admitted = await harness.Service.UpdateAsync(stopped.Id, stopped.Version, Command(target)).ConfigureAwait(false);
        var after = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Stopped).ConfigureAwait(false);

        AssertEx.Equal(ExternalAppDesiredState.Stopped, after.DesiredState);

        var containers = await harness.Runtime
                                      .ListContainersDetailedAsync(ExternalAppLabels.For(harness.Service.InstallId, after.Id))
                                      .ConfigureAwait(false);

        AssertEx.Equal(expected: 2, containers.Count, "The update must have built both of the target's services.");
        AssertEx.Empty(containers.Where(static container => string.Equals(container.State, "running", StringComparison.Ordinal))
                                 .Select(static container => container.Id));
    }

    private static ApplicationManifest Version1()
    {
        return ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")]);
    }

    private static ApplicationManifest Version2()
    {
        return ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")], manifestVersion: 2);
    }

    private static UpdateCommand Command(ApplicationManifest target)
    {
        return new UpdateCommand(target.ManifestVersion,
            target.ManifestSha256,
            AcceptPermissions: true,
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static async Task<ExternalAppServiceHarness> InstalledAsync(ApplicationManifest manifest,
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
}
