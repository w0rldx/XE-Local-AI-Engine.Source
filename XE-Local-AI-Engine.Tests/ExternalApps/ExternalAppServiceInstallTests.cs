namespace XE_Local_AI_Engine.Tests.ExternalApps;

using System.Globalization;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The install pipeline end to end against the lying container fake: the happy path, every admission refusal,
///     and each way a daemon can make an install fail after the row exists.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ExternalAppServiceInstallTests
{
    private const string AppId = "test-app";

    [Test]
    public async Task Install_OnTheHappyPath_RunsTheInstanceAndRecordsTheObservedBindings()
    {
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("web",
                ports: [ExternalAppTestManifests.UiPort(8080)],
                storage: [new ApplicationStorage("data", "/var/lib/app")])
        ]);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);

        var admitted = await harness.Service.InstallAsync(Command(manifest));

        AssertEx.Equal(ExternalAppInstanceStatus.Installing, admitted.Status);
        AssertEx.Equal(ExternalAppDesiredState.Stopped, admitted.DesiredState);

        var row = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Running);
        AssertEx.Equal(ExternalAppDesiredState.Running, row.DesiredState);
        AssertEx.Null(row.FailureCategory);
        AssertEx.False(row.NeedsRecreate, "A freshly installed instance has nothing to recreate.");

        // The bindings are the daemon's answer, not the plan's: the fake assigns its own host port on start, and
        // persisting the requested one would report an address nothing is listening on.
        var published = ExternalAppPublishedPorts.Parse(row.PublishedPortsJson);
        AssertEx.Equal(expected: 1, published.Count);
        AssertEx.Equal("web", published[0].Service);
        AssertEx.Equal(expected: 8080, published[0].ContainerPort);
        AssertEx.True(published[0].HostPort > 0, "The observed host port must be a real port.");

        var kinds = (await harness.ReadEventsAsync(admitted.Id)).Select(entry => entry.Kind);
        AssertEx.Equal("PermissionAccepted, Installed, Started", string.Join(", ", kinds));

        AssertEx.Equal(expected: 3, harness.Publisher.Events.Count);
        AssertEx.True(harness.Runtime.ProbedWritablePaths.Count == 1, "The write probe runs exactly once, on the first started service that has storage.");
        AssertEx.Equal("/var/lib/app", harness.Runtime.ProbedWritablePaths[0].ContainerPath);
    }

    [Test]
    public async Task Install_WhenTheManifestRequiresAGpu_IsRefusedWithGpuNotSupported()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")],
            permissions: new ApplicationPermissions(Internet: true, LocalNetwork: false, "none", "required"));

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);

        var failure = await AssertEx.ThrowsAsync<ExternalAppValidationException>(() => harness.Service.InstallAsync(Command(manifest)));

        AssertEx.Contains(failure.Message, nameof(ExternalAppBlockedReason.GpuNotSupported));
        AssertEx.Empty(harness.Runtime.CreatedContainerIds, "Nothing is created for an application this engine cannot run.");
    }

    [Test]
    public async Task Install_WhenTheRuntimeLacksARequiredCapability_IsRefusedNamingIt()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")],
            requires: ["containers", "gpuDevices"]);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);

        var failure = await AssertEx.ThrowsAsync<ExternalAppValidationException>(() => harness.Service.InstallAsync(Command(manifest)));

        AssertEx.Contains(failure.Message, nameof(ExternalAppBlockedReason.RuntimeIncompatible));
        AssertEx.Contains(failure.Message, "gpuDevices");
    }

    [Test]
    public async Task Install_WhenNoRuntimeIsReady_IsRefusedWithTheResolutionsOwnMessage()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")]);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);
        harness.Resolver.Resolution = FakeContainerRuntimeResolver.UnavailableResolution("Nothing answered at unix:///var/run/docker.sock.");

        var failure = await AssertEx.ThrowsAsync<ExternalAppValidationException>(() => harness.Service.InstallAsync(Command(manifest)));

        AssertEx.Contains(failure.Message, nameof(ExternalAppBlockedReason.RuntimeUnavailable));

        // The resolution's own prose, not a second description of the same state written at the refusal site.
        AssertEx.Contains(failure.Message, "Nothing answered at unix:///var/run/docker.sock.");
    }

    [Test]
    public async Task Install_WithTooLittleMemory_IsRefusedWithInsufficientMemory()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")],
            resources: new ApplicationResources(MinimumMemoryMb: 8192, RecommendedMemoryMb: 16384, CpuHint: 2, PidsLimit: 512));

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest, availableRamBytes: 1024L * 1024 * 1024);

        var failure = await AssertEx.ThrowsAsync<ExternalAppValidationException>(() => harness.Service.InstallAsync(Command(manifest)));

        AssertEx.Contains(failure.Message, nameof(ExternalAppBlockedReason.InsufficientMemory));
    }

    /// <summary>
    ///     Install is refused for the same reason an update is, and at the same place: the manifest reads a bridge
    ///     built-in this node cannot supply. Left to the pipeline it would fail after the row exists, leaving a
    ///     failed instance where the catalog page could simply have said the node cannot run this.
    /// </summary>
    [Test]
    public async Task Install_OnANodeWithNoBridge_ForAManifestThatNeedsOne_IsRefusedAndThePreviewSaysSoFirst()
    {
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("web",
                environment: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["OPENAI_BASE_URL"] = "http://${XE_BRIDGE_ENDPOINT}/llm/v1"
                })
        ]);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest, withBridge: false);

        var preview = await harness.Service.PreviewInstallAsync(manifest.Id);
        AssertEx.False(preview.CanInstall, "The catalog page must not offer an install the pipeline cannot finish.");
        AssertEx.Equal(ExternalAppBlockedReason.BridgeUnavailable, preview.BlockedReason);

        var failure = await AssertEx.ThrowsAsync<ExternalAppValidationException>(() => harness.Service.InstallAsync(Command(manifest)));

        AssertEx.Contains(failure.Message, nameof(ExternalAppBlockedReason.BridgeUnavailable));
        AssertEx.Contains(failure.Message, "container bridge", message: "The operator has to read which feature is missing.");
    }

    [Test]
    public async Task Install_WithAMissingRequiredVariable_NamesItAndNeverItsValue()
    {
        var manifest = ExternalAppTestManifests.Manifest([
                ExternalAppTestManifests.Service("web", environment: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PW"] = "${ADMIN_PASSWORD}"
                })
            ],
            variables: [ExternalAppTestManifests.Variable("ADMIN_PASSWORD", required: true, type: "secret")]);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);

        var failure = await AssertEx.ThrowsAsync<ExternalAppValidationException>(() => harness.Service.InstallAsync(Command(manifest)));

        AssertEx.Contains(failure.Names, "ADMIN_PASSWORD");
        AssertEx.Contains(failure.Message, "ADMIN_PASSWORD");
    }

    [Test]
    public async Task Install_WithAnUndeclaredVariableKey_IsRefusedRatherThanSilentlyDropped()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")]);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);

        var failure = await AssertEx.ThrowsAsync<ExternalAppValidationException>(() => harness.Service.InstallAsync(Command(manifest,
            variables: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ADMlN_PASSWORD"] = "hunter2"
            })));

        // A mistyped key is most often the name of the field holding a password. Dropping it would install the
        // application with a blank one.
        AssertEx.Contains(failure.Names, "ADMlN_PASSWORD");
        AssertEx.False(failure.Message.Contains("hunter2", StringComparison.Ordinal), "A validation message must never echo a submitted value.");
    }

    [Test]
    public async Task Install_WithoutAcceptPermissions_CarriesTheWholeDeclaredPermissionSet()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web", ports: [ExternalAppTestManifests.UiPort(8080)], capAdd: ["CHOWN"])],
            permissions: new ApplicationPermissions(Internet: true, LocalNetwork: true, "readOnly", "none"));

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);

        var failure = await AssertEx.ThrowsAsync<ExternalAppPermissionChangeRequiresAcknowledgementException>(() => harness.Service.InstallAsync(Command(manifest, acceptPermissions: false)));

        AssertEx.Contains(failure.AddedPermissions, "internet");
        AssertEx.Contains(failure.AddedPermissions, "localNetwork");
        AssertEx.Contains(failure.AddedPermissions, "hostFiles");
        AssertEx.Contains(failure.AddedPermissions, "capabilities");
        AssertEx.Contains(failure.AddedPermissions, "publishedPorts");
        AssertEx.Contains(failure.AddedPermissions, "writableRootFilesystem");
    }

    [Test]
    public async Task Install_WithAStaleManifestSha_Is409ManifestChanged()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")]);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);

        var failure = await AssertEx.ThrowsAsync<ExternalAppManifestChangedException>(() => harness.Service.InstallAsync(Command(manifest) with
        {
            ManifestSha256 = new string('b', 64)
        }));

        AssertEx.Equal(manifest.ManifestSha256, failure.ManifestSha256);
        AssertEx.Equal(manifest.ManifestVersion, failure.ManifestVersion);
    }

    [Test]
    public async Task Install_WithAStaleManifestVersion_Is409ManifestChanged()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")], manifestVersion: 3);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);

        _ = await AssertEx.ThrowsAsync<ExternalAppManifestChangedException>(() => harness.Service.InstallAsync(Command(manifest) with
        {
            ManifestVersion = 2
        }));
    }

    [Test]
    public async Task Install_OfAnAlreadyInstalledApplication_Is409AlreadyInstalled()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")]);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);

        var first = await harness.Service.InstallAsync(Command(manifest));
        _ = await harness.SettleAsync(first.Id, ExternalAppInstanceStatus.Running);

        _ = await AssertEx.ThrowsAsync<ExternalAppAlreadyInstalledException>(() => harness.Service.InstallAsync(Command(manifest)));
    }

    /// <summary>
    ///     Every install mints a fresh instance id and takes that key's gate before the authoritative
    ///     already-installed check. A refusal releases the lease, and the map entry has to go with it: nothing will
    ///     ever carry that id, so an entry left behind is one more for every rejected install the node ever answers.
    /// </summary>
    [Test]
    public async Task Install_WhenAdmissionIsRefused_LeavesNoEntryBehindInTheGateMap()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")]);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);

        var first = await harness.Service.InstallAsync(Command(manifest));
        _ = await harness.SettleAsync(first.Id, ExternalAppInstanceStatus.Running);

        // The installed instance's key and the application key install takes around its check: both bounded by what
        // is installed, which is what makes any growth from here a leak.
        var tracked = harness.Gate.TrackedCount;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            _ = await AssertEx.ThrowsAsync<ExternalAppAlreadyInstalledException>(() => harness.Service.InstallAsync(Command(manifest)));
        }

        AssertEx.Equal(tracked, harness.Gate.TrackedCount, "Each refused install left the gate it minted for an id no instance will ever have.");
    }

    [Test]
    public async Task Install_WhenThePullFails_LandsOnImagePullFailedAndKeepsTheStorage()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web", storage: [new ApplicationStorage("data", "/var/lib/app")])]);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);
        harness.Runtime.PullFailure = new DockerRuntimeException("The registry refused the pull.");

        var admitted = await harness.Service.InstallAsync(Command(manifest));
        var row = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Failed);

        AssertEx.Equal(ExternalAppFailureCategory.ImagePullFailed, row.FailureCategory);
        AssertEx.Empty(harness.Runtime.CreatedContainerIds, "A failed pull creates no container.");

        // The pull is step 8 and the network step 9, so a pull that fails leaves nothing on the daemon at all.
        AssertEx.Empty(harness.Runtime.CreatedNetworks, "A failed pull never reaches the network.");
    }

    [Test]
    public async Task Install_WhenTheReadBackViolatesPolicy_LandsOnPolicyViolationAndRemovesTheContainer()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")]);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);

        // The daemon claims it created a privileged container. Nothing the engine asked for could produce that, and
        // a container in that state is one whose confinement was never established.
        harness.Runtime.InspectionMutator = inspection => inspection with
        {
            Privileged = true
        };

        var admitted = await harness.Service.InstallAsync(Command(manifest));
        var row = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Failed);

        AssertEx.Equal(ExternalAppFailureCategory.PolicyViolation, row.FailureCategory);
        AssertEx.NotEmpty(harness.Runtime.RemovedContainerIds);
        AssertEx.Empty(harness.Runtime.CreatedNetworks, "The instance network goes with the containers on a failed attempt.");

        // Storage survives a failed attempt: deleting it is a data-destroying act reserved for reset and uninstall.
        AssertEx.True(Directory.Exists(row.StoragePath), $"The instance directory '{row.StoragePath}' must survive a failed install.");
    }

    [Test]
    public async Task Install_WhenTheRuntimeRefusesOnPolicyGrounds_LandsOnPolicyViolation()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")]);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);
        harness.Runtime.RunFailure = _ => throw new ContainerPolicyException(ContainerPolicyException.ForeignNetworkReason,
            "The network name is in use by a foreign container network.");

        var admitted = await harness.Service.InstallAsync(Command(manifest));
        var row = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Failed);

        AssertEx.Equal(ExternalAppFailureCategory.PolicyViolation, row.FailureCategory);
    }

    [Test]
    public async Task Install_WhenAFileHashIsWrong_LandsOnStorageError()
    {
        var good = ExternalAppTestManifests.File("settings.yml", "/etc/app/settings.yml", "server: local\n");
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("web", files:
            [
                good with
                {
                    Sha256 = new string('c', 64)
                }
            ])
        ]);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);

        var admitted = await harness.Service.InstallAsync(Command(manifest));
        var row = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Failed);

        AssertEx.Equal(ExternalAppFailureCategory.StorageError, row.FailureCategory);
        AssertEx.Empty(harness.Runtime.CreatedContainerIds, "Storage is materialised before any container is created.");
    }

    [Test]
    public async Task Install_WhenTheManifestHasCollidingMounts_FailsBeforeAnyFileIsWritten()
    {
        // Two services claiming the SAME host source. The plan runs at step 10 and storage at step 11, so this is
        // refused with the instance's files directory still absent.
        var shared = new ApplicationStorage("data", "/var/lib/app");
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("web",
                storage: [shared],
                files: [ExternalAppTestManifests.File("settings.yml", "/etc/app/settings.yml", "server: local\n")]),
            ExternalAppTestManifests.Service("web-2", storage: [shared, shared], image: ExternalAppTestManifests.SecondImage)
        ]);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);

        var admitted = await harness.Service.InstallAsync(Command(manifest));
        var row = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Failed);

        AssertEx.Equal(ExternalAppFailureCategory.ConfigurationMissing, row.FailureCategory);
        AssertEx.False(Directory.Exists(Path.Combine(row.StoragePath, "files")),
            "A manifest the planner rejects must not leave half-written assets behind.");
    }

    [Test]
    public async Task Install_WhenTheWriteProbeReturnsFalse_FailsWithStorageErrorNamingTheDaemonMode()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web", storage: [new ApplicationStorage("data", "/var/lib/app")])]);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);
        harness.Runtime.WritableProbeOutcome = (_, _) => false;

        var admitted = await harness.Service.InstallAsync(Command(manifest));
        var row = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Failed);

        AssertEx.Equal(ExternalAppFailureCategory.StorageError, row.FailureCategory);
        AssertEx.Contains(row.FailureSummary, "rootless");
    }

    [Test]
    public async Task Install_WhenAHealthyDependencyNeverTurnsHealthy_FailsWithHealthCheckFailed()
    {
        var manifest = HealthyDependencyManifest();

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);

        // The daemon keeps answering "starting". The engine is not allowed to decide that is good enough.
        harness.Runtime.InspectionMutator = inspection =>
            inspection with
            {
                State = inspection.State with
                {
                    Health = ContainerHealthState.Starting
                }
            };

        var admitted = await harness.Service.InstallAsync(Command(manifest));
        await AdvancePastTheReadyDeadlineAsync(harness, admitted.Id);

        var row = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Failed);
        AssertEx.Equal(ExternalAppFailureCategory.HealthCheckFailed, row.FailureCategory);
    }

    /// <summary>
    ///     A health value this engine does not recognise reads as <c>None</c> on a service that DECLARES a
    ///     healthcheck. That is "not healthy yet", never "healthy": treating the unknown as success would start every
    ///     dependant against a database that has not opened its socket.
    /// </summary>
    [Test]
    public async Task Install_WithAnUnrecognisedHealthValue_WaitsToTheDeadlineRatherThanProceeding()
    {
        var manifest = HealthyDependencyManifest();

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);
        harness.Runtime.InspectionMutator = inspection =>
            inspection with
            {
                State = inspection.State with
                {
                    Health = ContainerHealthState.None
                }
            };

        var admitted = await harness.Service.InstallAsync(Command(manifest));
        await AdvancePastTheReadyDeadlineAsync(harness, admitted.Id);

        var row = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Failed);
        AssertEx.Equal(ExternalAppFailureCategory.HealthCheckFailed, row.FailureCategory);
    }

    [Test]
    public async Task Install_WhenAHealthcheckReportsUnhealthy_FailsWithoutWaitingOutTheDeadline()
    {
        var manifest = HealthyDependencyManifest();

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);
        harness.Runtime.InspectionMutator = inspection =>
            inspection with
            {
                State = inspection.State with
                {
                    Health = ContainerHealthState.Unhealthy
                }
            };

        var admitted = await harness.Service.InstallAsync(Command(manifest));

        // The clock is never advanced: an unhealthy answer is a verdict, not a slow start.
        var row = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Failed);
        AssertEx.Equal(ExternalAppFailureCategory.HealthCheckFailed, row.FailureCategory);
    }

    [Test]
    public async Task Install_WhenAServiceExitsWhileWaitedOn_FailsImmediately()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")]);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);
        harness.Runtime.ExitState = _ => new ContainerRunState
        {
            Running = false,
            Status = "exited",
            ExitCode = 137,
            OutOfMemoryKilled = false,
            Health = ContainerHealthState.None
        };

        var admitted = await harness.Service.InstallAsync(Command(manifest));

        var row = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Failed);
        AssertEx.Equal(ExternalAppFailureCategory.HealthCheckFailed, row.FailureCategory);
        AssertEx.Contains(row.FailureSummary, "exited");
    }

    /// <summary>
    ///     A port taken between the probe and the create replans the WHOLE attempt. Reassigning only the port that
    ///     lost would leave the dependent service's environment pointing at the old number — this asserts the
    ///     replanned value is what actually reached the container.
    /// </summary>
    [Test]
    public async Task Install_WhenACreateLosesThePortRace_TearsDownReplansAndRetriesOnce()
    {
        var manifest = PortRaceManifest();

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);

        var requested = new List<ContainerSpecification>();
        var raced = 0;
        harness.Runtime.RunFailure = specification =>
        {
            requested.Add(specification);
            if (raced > 0 || specification.PublishedPorts.Count == 0)
            {
                return null;
            }

            raced++;

            // Take the port the engine just let go of, then fail the create the way a daemon does when it cannot
            // bind. The failure translator's re-probe is what recognises this as a lost race.
            harness.Squat(specification.PublishedPorts[0].HostPort!.Value);
            return new DockerRuntimeException("The host port is already allocated.");
        };

        var admitted = await harness.Service.InstallAsync(Command(manifest));
        var row = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Running);

        AssertEx.Equal(expected: 1, raced);

        var published = ExternalAppPublishedPorts.Parse(row.PublishedPortsJson);
        var webPort = published.Single(port => string.Equals(port.Service, "web", StringComparison.Ordinal)).HostPort;

        // The dependant's environment was rebuilt from the NEW port. Reassigning only the port that lost would have
        // left this value pointing at an address nothing is listening on.
        var sidecar = requested.Last(specification => specification.Name.EndsWith("-sidecar", StringComparison.Ordinal));
        AssertEx.Equal("http://127.0.0.1:" + webPort.ToString(CultureInfo.InvariantCulture), sidecar.Environment["WEB_URL"]);
    }

    [Test]
    public async Task Install_WhenTheSecondAttemptAlsoLosesTheRace_FailsWithPortUnavailable()
    {
        var manifest = PortRaceManifest();

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);

        harness.Runtime.RunFailure = specification =>
        {
            if (specification.PublishedPorts.Count == 0)
            {
                return null;
            }

            harness.Squat(specification.PublishedPorts[0].HostPort!.Value);
            return new DockerRuntimeException("The host port is already allocated.");
        };

        var admitted = await harness.Service.InstallAsync(Command(manifest));
        var row = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Failed);

        AssertEx.Equal(ExternalAppFailureCategory.PortUnavailable, row.FailureCategory);
    }

    /// <summary>
    ///     Docker binds the host port on START, not on create, so a race can surface after siblings already exist.
    ///     The whole attempt is torn down and replanned — never the one container that failed.
    /// </summary>
    [Test]
    public async Task Install_WhenAStartLosesThePortRaceAfterSiblingsWereCreated_TearsDownTheWholeAttemptAndReplans()
    {
        var manifest = PortRaceManifest();

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);

        var squatted = new List<int>();
        harness.Gated.StartFailure = containerId =>
        {
            if (squatted.Count != 0)
            {
                return null;
            }

            var inspection = harness.Runtime.InspectAsync(containerId).GetAwaiter().GetResult();
            if (inspection.RequestedPortBindings.Count == 0)
            {
                return null;
            }

            var port = inspection.RequestedPortBindings[0].HostPort!.Value;
            squatted.Add(port);
            harness.Squat(port);
            return new DockerRuntimeException("The host port is already allocated.");
        };

        var admitted = await harness.Service.InstallAsync(Command(manifest));
        var row = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Running);

        AssertEx.NotEmpty(squatted, "The test never produced the start-phase race it exists to cover.");

        // BOTH containers of the attempt are gone, not just the one whose start failed.
        AssertEx.Equal(expected: 2, harness.Runtime.RemovedContainerIds.Count);

        var published = ExternalAppPublishedPorts.Parse(row.PublishedPortsJson);
        AssertEx.NotEqual(squatted[0], published.Single(port => string.Equals(port.Service, "web", StringComparison.Ordinal)).HostPort);
    }

    /// <summary>
    ///     The post-start read-back on its own. <c>RequestedPortBindings</c> is what the daemon was ASKED to bind and
    ///     is populated at create; <c>PublishedPorts</c> is what it actually bound. Widening only the second is the
    ///     exact gap a pre-start-only check leaves open, and it must fail the install.
    /// </summary>
    [Test]
    public async Task Install_WhenOnlyTheAppliedBindingIsWidened_FailsWithAPolicyViolation()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web", ports: [ExternalAppTestManifests.UiPort(8080)])]);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);

        // The request still says loopback; only the applied binding is off it.
        harness.Runtime.InspectionMutator = static inspection => inspection with
        {
            PublishedPorts =
            [
                .. inspection.PublishedPorts.Select(static port => port with
                {
                    HostIp = "0.0.0.0"
                })
            ]
        };

        var admitted = await harness.Service.InstallAsync(Command(manifest));
        var row = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Failed);

        AssertEx.Equal(ExternalAppFailureCategory.PolicyViolation, row.FailureCategory);
        var failureSummary = AssertEx.NotNull(row.FailureSummary);
        AssertEx.Contains(failureSummary, "1 policy check");

        // The stored summary counts the violations and quotes none of them — a sibling violation names a host path,
        // and this column is rendered in a browser. The node log is where the offending binding is stated.
        AssertEx.False(failureSummary.Contains("0.0.0.0", StringComparison.Ordinal), "The stored summary quotes no violation.");
        AssertEx.True(harness.ServiceLog.Entries.Any(entry => entry.Message.Contains("0.0.0.0", StringComparison.Ordinal)),
            "The violation itself must still reach the node log, or the summary's silence loses the diagnosis.");
        AssertEx.NotEmpty(harness.Runtime.RemovedContainerIds, "A container that failed the read-back is torn down, never left serving.");
    }

    /// <summary>
    ///     A secret variable's VALUE must reach exactly two places: the encrypted column and the container's own
    ///     environment. Not a log line, and not an event's detail document — both of which are read by people who
    ///     are not the person who typed it, and the failure path is where a value most easily escapes into prose.
    /// </summary>
    [Test]
    public async Task Install_AcrossTheFailurePath_NeverWritesASecretValueToALogOrToAnEventDetail()
    {
        const string secret = "hunter2-scarlet-pimpernel-1789";
        var manifest = ExternalAppTestManifests.Manifest([
                ExternalAppTestManifests.Service("web", environment: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PW"] = "${ADMIN_PASSWORD}"
                })
            ],
            variables: [ExternalAppTestManifests.Variable("ADMIN_PASSWORD", required: true, type: "secret")]);

        await using var harness = await ExternalAppServiceHarness.CreateAsync(manifest);

        var specifications = new List<ContainerSpecification>();
        harness.Runtime.RunFailure = specification =>
        {
            specifications.Add(specification);
            return null;
        };

        // Fails AFTER the container exists, so the run produces a failure summary and a teardown rather than an
        // admission refusal that never reaches the variables at all.
        harness.Gated.StartFailure = static _ => new DockerRuntimeException("The daemon refused to start the container.");

        var admitted = await harness.Service
                                    .InstallAsync(Command(manifest,
                                        new Dictionary<string, string>(StringComparer.Ordinal)
                                        {
                                            ["ADMIN_PASSWORD"] = secret
                                        }));
        var row = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Failed);

        // The control: without these two, the test would pass on an install that never saw the secret at all.
        AssertEx.Contains(row.VariablesJson, secret);
        AssertEx.NotEmpty(specifications);
        AssertEx.Equal(secret, specifications[0].Environment["PW"], "The container's own environment is the other place the value belongs.");

        AssertEx.False(AssertEx.NotNull(row.FailureSummary).Contains(secret, StringComparison.Ordinal), "The failure summary must not carry the value.");

        // Both scans pass vacuously on an empty collection, so each is preceded by the proof that it has something to
        // scan: an install that logged nothing and published nothing would otherwise report this test green.
        AssertEx.NotEmpty(harness.ServiceLog.Entries, "The failure path must have written log lines for this scan to mean anything.");
        foreach (var entry in harness.ServiceLog.Entries)
        {
            AssertEx.False(entry.Message.Contains(secret, StringComparison.Ordinal), $"A log line carried the secret: {entry.Message}");
            AssertEx.False(entry.Exception?.ToString().Contains(secret, StringComparison.Ordinal) == true, "A logged exception carried the secret.");
        }

        var events = await harness.ReadEventsAsync(admitted.Id);
        AssertEx.NotEmpty(events, "The failure path must have published events for this scan to mean anything.");
        foreach (var published in events)
        {
            AssertEx.False(published.DetailJson?.Contains(secret, StringComparison.Ordinal) == true,
                $"The {published.Kind} event's detail carried the secret.");
        }
    }

    private static ApplicationManifest PortRaceManifest()
    {
        return ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("web", ports: [ExternalAppTestManifests.UiPort(8080)]),
            ExternalAppTestManifests.Service("sidecar",
                environment: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["WEB_URL"] = "http://127.0.0.1:${XE_UI_HOST_PORT_web}"
                },
                dependsOn: [new ApplicationDependency("web", "started")],
                image: ExternalAppTestManifests.SecondImage)
        ]);
    }

    private static ApplicationManifest HealthyDependencyManifest()
    {
        return ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("db",
                healthcheck: new ApplicationHealthcheck(["CMD", "true"], IntervalSeconds: 5, TimeoutSeconds: 2, Retries: 3, StartPeriodSeconds: 1)),
            ExternalAppTestManifests.Service("web",
                dependsOn: [new ApplicationDependency("db", "healthy")],
                image: ExternalAppTestManifests.SecondImage)
        ]);
    }

    /// <summary>
    ///     Moves the harness clock forward until the ready deadline fires. The wait is driven by the injected time
    ///     provider, so this is the test deciding the deadline has passed rather than the test sleeping through it.
    /// </summary>
    private static async Task AdvancePastTheReadyDeadlineAsync(ExternalAppServiceHarness harness, Guid instanceId)
    {
        await AssertEx.EventuallyAsync(() =>
            {
                harness.Time.Advance(TimeSpan.FromSeconds(5));
                return harness.ReadAsync(instanceId).GetAwaiter().GetResult() is { Status: ExternalAppInstanceStatus.Failed };
            },
            TestBudgets.Contended,
            "Advancing the clock past the service-ready deadline never settled the instance.");
    }

    private static InstallCommand Command(ApplicationManifest manifest,
        IReadOnlyDictionary<string, string>? variables = null,
        bool acceptPermissions = true)
    {
        return new InstallCommand(AppId,
            DisplayName: null,
            manifest.ManifestVersion,
            manifest.ManifestSha256,
            variables ?? new Dictionary<string, string>(StringComparer.Ordinal),
            acceptPermissions);
    }
}
