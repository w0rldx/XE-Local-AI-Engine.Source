namespace XE_Local_AI_Engine.Tests.Containers;

using System.Globalization;
using System.Text;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Fake;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The lying fake as the subject, because it is the seam every later slice's tests stand on.
///     <para>
///         What is asserted here is what only the fake can be asked: that each hook actually produces the lie it
///         advertises — a daemon cannot be asked to report a capability nobody requested, an anonymous volume, or a
///         mount the container cannot write, so a fail-closed branch above this layer is reachable only through these
///         — and the refusals the fake makes on the state it holds rather than on the request, which on the daemon
///         path are 404s and 409s off the wire and so cannot be a shared contract case.
///     </para>
///     <para>
///         The refusals the fake makes on the REQUEST — a non-loopback host IP, an unpinned image, a blank user, a
///         duplicated port, an unfiltered list — are no longer asserted here. They are the contract both
///         implementations owe, so they live once in <see cref="ContainerRuntimeContractTests" />, which runs them
///         against the fake and the production client alike; a copy here would be the pinning that let two drifts
///         through.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ContainerRuntimeFakeContractTests
{
    private const string Digest = "@sha256:0000000000000000000000000000000000000000000000000000000000000000";
    private const string Image = "ghcr.io/example/app" + Digest;

    [Test]
    [Arguments("127.0.0.1")]
    public async Task RunContainer_WithALoopbackHostIp_IsAccepted(string hostIp)
    {
        var client = Client();

        var containerId = await client.RunContainerAsync(Specification() with
        {
            PublishedPorts =
            [
                new ContainerPortPublication
                {
                    ContainerPort = 8080,
                    HostIp = hostIp,
                    HostPort = 30080
                }
            ]
        });

        AssertEx.NotNullOrEmpty(containerId);
    }

    [Test]
    public async Task RunContainer_PublishingOneContainerPortOnBothProtocols_IsAccepted()
    {
        // The key is port AND protocol: 53/tcp and 53/udp are two exposed ports, and a guard that refused them would
        // refuse the shape every DNS-ish application asks for.
        var client = Client();

        var containerId = await client.RunContainerAsync(Specification() with
        {
            PublishedPorts =
            [
                new ContainerPortPublication
                {
                    ContainerPort = 53,
                    HostIp = "127.0.0.1",
                    HostPort = 30053
                },
                new ContainerPortPublication
                {
                    ContainerPort = 53,
                    HostIp = "127.0.0.1",
                    HostPort = 30053,
                    Protocol = "udp"
                }
            ]
        });

        AssertEx.NotNullOrEmpty(containerId);
    }

    [Test]
    public async Task RunContainer_ThenInspect_ReportsBackWhatWasAsked()
    {
        var client = Client();
        var specification = Specification();

        var containerId = await client.RunContainerAsync(specification);
        var inspection = await client.InspectAsync(containerId);

        AssertEx.Equal(specification.Name, inspection.Name);
        AssertEx.Equal(specification.Image, inspection.Image);
        AssertEx.Equal(specification.NetworkName, inspection.NetworkMode);
        AssertEx.Equal("ALL", string.Join(",", inspection.CapabilitiesDropped));
        AssertEx.Equal(ContainerRestartMode.UnlessStopped, inspection.RestartMode);
        AssertEx.False(inspection.Privileged);
        AssertEx.Equal(expected: 0, inspection.DeviceCount);
    }

    [Test]
    public async Task RunContainer_SetsNoUserByDefault()
    {
        // Production never sets a user: the curated images start as in-container root and drop privileges through
        // their own entrypoints, and forcing a uid breaks them and breaks a port-80 bind.
        var client = Client();

        var inspection = await client.InspectAsync(await client.RunContainerAsync(Specification()));

        AssertEx.Equal(string.Empty, inspection.User);
    }

    [Test]
    public async Task RunContainer_SetsNoMemoryOrCpuCeiling()
    {
        var client = Client();

        var inspection = await client.InspectAsync(await client.RunContainerAsync(Specification()));

        // The manifest's memory figures are admission-gate inputs, not per-container limits. Pids is the one ceiling
        // V1 really imposes, and the verifier asserts the two zeros rather than assuming them.
        AssertEx.Equal(expected: 0L, inspection.MemoryBytes);
        AssertEx.Equal(expected: 0L, inspection.NanoCpus);
        AssertEx.Equal(expected: 512L, inspection.PidsLimit);
    }

    [Test]
    public async Task Inspect_ReportsNanoCpusAndReadOnlyRootFilesystem()
    {
        var client = Client();

        var inspection = await client.InspectAsync(await client.RunContainerAsync(Specification() with
        {
            ReadOnlyRootFilesystem = true,
            NanoCpus = 0
        }));

        AssertEx.True(inspection.ReadOnlyRootFilesystem);
        AssertEx.Equal(expected: 0L, inspection.NanoCpus);
    }

    [Test]
    public async Task RequestedPortBindings_ArePresentBeforeStartAndPublishedPortsAreNot()
    {
        var client = Client();
        var containerId = await client.RunContainerAsync(WithPort(preferredHostPort: 30080));

        var inspection = await client.InspectAsync(containerId);

        AssertEx.Equal(expected: 1, inspection.RequestedPortBindings.Count);
        AssertEx.Equal("127.0.0.1", inspection.RequestedPortBindings[0].HostIp);
        // Empty before start, which is the whole reason two port sets exist: a fake filling both at create would pass
        // a two-pass verification in tests and fail against a real daemon.
        AssertEx.Empty(inspection.PublishedPorts);
    }

    [Test]
    public async Task PublishedPorts_AppearOnlyAfterStart()
    {
        var client = Client();
        var containerId = await client.RunContainerAsync(WithPort(preferredHostPort: 30080));

        await client.StartContainerAsync(containerId);
        var inspection = await client.InspectAsync(containerId);

        AssertEx.Equal(expected: 1, inspection.PublishedPorts.Count);
        AssertEx.Equal(expected: 30080, inspection.PublishedPorts[0].HostPort);
        AssertEx.Equal("127.0.0.1", inspection.PublishedPorts[0].HostIp);
    }

    [Test]
    public async Task PublishedPorts_AreAssignedOnceAndReadTheSameOnEveryInspection()
    {
        // A host port the specification leaves unset is the daemon's to choose, and a daemon chooses it ONCE — at
        // start. A fake that chose a fresh one per inspection made a running application appear to move ports
        // between a post-start verification and the next status read, which is a lie no daemon can tell.
        var client = Client();
        var containerId = await client.RunContainerAsync(WithPort(preferredHostPort: null));

        await client.StartContainerAsync(containerId);
        var first = await client.InspectAsync(containerId);
        var second = await client.InspectAsync(containerId);

        AssertEx.Equal(expected: 1, first.PublishedPorts.Count);
        AssertEx.Equal(first.PublishedPorts[0].HostPort, second.PublishedPorts[0].HostPort,
            "Two inspections of one running container reported different host ports.");
    }

    [Test]
    public async Task AssignedHostPorts_ReportsAPortOtherThanThePreferredOne()
    {
        var client = Client();
        client.AssignedHostPorts = _ => 45123;
        var containerId = await client.RunContainerAsync(WithPort(preferredHostPort: 30080));

        await client.StartContainerAsync(containerId);
        var inspection = await client.InspectAsync(containerId);

        // The daemon's answer wins over the preference, which is why the resolved port is read back rather than
        // assumed from the plan.
        AssertEx.Equal(expected: 45123, inspection.PublishedPorts[0].HostPort);
    }

    [Test]
    public async Task InspectionMutator_CanReportAHostIpOfZeroZeroZeroZero()
    {
        var client = Client();
        client.InspectionMutator = inspection => inspection with
        {
            PublishedPorts =
            [
                new ContainerPublishedPort
                {
                    ContainerPort = 8080,
                    Protocol = "tcp",
                    HostIp = "0.0.0.0",
                    HostPort = 8080
                }
            ]
        };
        var containerId = await client.RunContainerAsync(WithPort(preferredHostPort: 30080));
        await client.StartContainerAsync(containerId);

        var inspection = await client.InspectAsync(containerId);

        // The lie a real daemon cannot be asked to tell, and the only way a caller's post-start check has a failing
        // case to catch.
        AssertEx.Equal("0.0.0.0", inspection.PublishedPorts[0].HostIp);
    }

    [Test]
    public async Task InspectionMutator_CanReportACapabilityThatWasNotRequested()
    {
        var client = Client();
        client.InspectionMutator = inspection => inspection with
        {
            CapabilitiesAdded = ["SYS_ADMIN"]
        };

        var inspection = await client.InspectAsync(await client.RunContainerAsync(Specification()));

        AssertEx.Contains(inspection.CapabilitiesAdded, "SYS_ADMIN");
    }

    [Test]
    public async Task Inspect_ReportsAnAnonymousVolumeTheSpecificationDidNotAskFor()
    {
        var client = Client();
        client.AnonymousMounts = _ =>
        [
            new ContainerMountView
            {
                Type = "volume",
                Source = "b2f9c1",
                Destination = "/var/lib/app",
                ReadOnly = false
            }
        ];

        var inspection = await client.InspectAsync(await client.RunContainerAsync(Specification()));

        // An image's own VOLUME instruction creates this, it appears in no request, and it is exactly the mount that
        // would put application data outside the instance directory unnoticed.
        AssertEx.ContainsSingle(inspection.Mounts, mount => mount.Type == "volume" && mount.Destination == "/var/lib/app");
    }

    [Test]
    public async Task HealthSequence_MovesFromStartingToHealthy()
    {
        var client = Client();
        client.HealthSequence.Enqueue(ContainerHealthState.Starting);
        client.HealthSequence.Enqueue(ContainerHealthState.Starting);
        client.HealthSequence.Enqueue(ContainerHealthState.Healthy);
        var containerId = await client.RunContainerAsync(Specification());

        var observed = new List<ContainerHealthState>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            observed.Add((await client.InspectAsync(containerId)).State.Health);
        }

        AssertEx.Equal("Starting,Starting,Healthy", string.Join(",", observed));
    }

    [Test]
    public async Task HealthSequence_CanEndUnhealthy()
    {
        var client = Client();
        client.HealthSequence.Enqueue(ContainerHealthState.Unhealthy);

        var inspection = await client.InspectAsync(await client.RunContainerAsync(Specification()));

        AssertEx.Equal(ContainerHealthState.Unhealthy, inspection.State.Health);
    }

    [Test]
    public async Task ExitState_ReportsAnExitCodeAndAnOutOfMemoryKill()
    {
        var client = Client();
        var containerId = await client.RunContainerAsync(Specification());
        client.ExitState = _ => new ContainerRunState
        {
            Running = false,
            Status = "exited",
            ExitCode = 137,
            OutOfMemoryKilled = true,
            Health = ContainerHealthState.None
        };

        var state = (await client.InspectAsync(containerId)).State;

        // The difference between "the application crashed" and "the box ran out of memory", which is what a
        // reconciler branches on and what it would otherwise have to guess.
        AssertEx.False(state.Running);
        AssertEx.Equal(expected: 137L, state.ExitCode);
        AssertEx.True(state.OutOfMemoryKilled);
    }

    [Test]
    public async Task PullImage_ReportsScriptedProgress()
    {
        var client = Client();
        client.PullProgressScript =
        [
            new ContainerPullProgress
            {
                ImageReference = Image,
                LayerCount = 3,
                CompletedLayers = 1,
                CurrentBytes = 100,
                TotalBytes = 300
            },
            new ContainerPullProgress
            {
                ImageReference = Image,
                LayerCount = 3,
                CompletedLayers = 3,
                CurrentBytes = 300,
                TotalBytes = 300
            }
        ];
        var reports = new List<ContainerPullProgress>();

        await client.PullImageAsync(Image, new RecordingProgress(reports));

        AssertEx.Equal(expected: 2, reports.Count);
        AssertEx.Equal(expected: 3, reports[^1].CompletedLayers);
        AssertEx.True(await client.ImageExistsAsync(Image));
    }

    [Test]
    public async Task PullFailure_Surfaces()
    {
        var client = EmptyDaemon();
        client.PullFailure = new DockerRuntimeException(DockerDaemonPreflightStatus.ProbeFailed, "the registry said no");

        var failure = await AssertEx.ThrowsAsync<DockerRuntimeException>(() => client.PullImageAsync(Image, progress: null));

        AssertEx.Equal("the registry said no", failure.Message);
        AssertEx.False(await client.ImageExistsAsync(Image));
    }

    [Test]
    public async Task StopContainer_OnAnAlreadyStoppedContainer_ReturnsFalse()
    {
        var client = Client();
        var containerId = await client.RunContainerAsync(Specification());
        await client.StartContainerAsync(containerId);

        AssertEx.True(await client.StopContainerAsync(containerId, TimeSpan.FromSeconds(5)));
        // False is "it was already stopped", which is an answer and not an error: a teardown must be able to run
        // twice without reasoning about how far the first attempt got.
        AssertEx.False(await client.StopContainerAsync(containerId, TimeSpan.FromSeconds(5)));
        AssertEx.Equal(TimeSpan.FromSeconds(5), client.StoppedGracePeriods[containerId]);
    }

    [Test]
    public async Task RemoveNetwork_OnAMissingNetwork_IsNotAnError()
    {
        var client = Client();

        await client.RemoveNetworkAsync("a-network-that-never-existed");

        AssertEx.Contains(client.RemovedNetworks, "a-network-that-never-existed");
    }

    [Test]
    public async Task CreateNetwork_OnANameConflictCarryingTheSameInstanceAndInstallLabels_ReturnsTheExistingId()
    {
        var client = EmptyDaemon();
        var specification = Network();

        var first = await client.CreateNetworkAsync(specification);
        var second = await client.CreateNetworkAsync(specification);

        // Re-entrancy after a crash between create and record: the second create of the same name with the same
        // labels is the same network, not a second one and not a refusal.
        AssertEx.Equal(first, second);
    }

    [Test]
    [Arguments("unrelated")]
    [Arguments("other-instance")]
    [Arguments("other-install")]
    public async Task CreateNetwork_OverAForeignNetwork_ThrowsContainerPolicyException(string flavour)
    {
        var client = EmptyDaemon();
        var labels = flavour switch
        {
            "unrelated" => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["com.example.owner"] = "someone-else"
            },
            "other-instance" => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["xe.instance"] = "someone-else",
                ["xe.install"] = "install-1"
            },
            _ => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["xe.instance"] = "instance-1",
                ["xe.install"] = "someone-else"
            }
        };
        await client.CreateNetworkAsync(Network() with
        {
            Labels = labels
        });

        var failure = await AssertEx.ThrowsAsync<ContainerPolicyException>(() => client.CreateNetworkAsync(Network()));

        // A name is not a capability: reusing a network on the strength of its name alone would attach the
        // application to somebody else's bridge with everything on it.
        AssertEx.Equal(ContainerPolicyException.ForeignNetworkReason, failure.Reason);
        AssertEx.Contains(failure.Detail, "xe-app-instance-1-net");
    }

    [Test]
    public async Task ListContainersDetailed_ReportsAnExitedContainerThatIsStillListed()
    {
        var client = Client();
        var containerId = await client.RunContainerAsync(Specification());
        await client.StartContainerAsync(containerId);
        await client.StopContainerAsync(containerId, TimeSpan.FromSeconds(1));
        client.ExitState = _ => new ContainerRunState
        {
            Running = false,
            Status = "exited",
            ExitCode = 7,
            OutOfMemoryKilled = false,
            Health = ContainerHealthState.None
        };

        var summaries = await client.ListContainersDetailedAsync(Labels());

        // The property the bare id list cannot express: a container stopped outside the engine is still listed, and
        // reading presence rather than state would report a crashed application as healthy.
        var summary = AssertEx.NotNull(summaries.SingleOrDefault(candidate => candidate.Id == containerId));
        AssertEx.Equal("exited", summary.State);
        AssertEx.Equal(expected: 7, summary.ExitCode);
    }

    /// <summary>
    ///     The three refusals a daemon makes on the state it holds rather than on the request. Without them the fake
    ///     would accept a create from an unpulled image, on an uncreated network, under a name already taken — and a
    ///     pipeline could prove an ordering it does not have.
    /// </summary>
    [Test]
    public async Task RunContainer_WithAnImageThatWasNeverPulled_IsRefusedAsNotFound()
    {
        var client = EmptyDaemon();
        client.SeedExistingNetwork(Network());

        var failure = await AssertEx.ThrowsAsync<DockerRuntimeException>(() => client.RunContainerAsync(Specification()));

        AssertEx.Contains(failure.Message, "NotFound");
        AssertEx.Contains(failure.Message, Image);
        AssertEx.Empty(client.CreatedContainerIds, "Refused before anything was created, the way a 404 leaves nothing behind.");
    }

    [Test]
    public async Task RunContainer_OnANetworkThatWasNeverCreated_IsRefusedAsNotFound()
    {
        var client = EmptyDaemon();
        client.SeedExistingImage(Image);

        var failure = await AssertEx.ThrowsAsync<DockerRuntimeException>(() => client.RunContainerAsync(Specification()));

        AssertEx.Contains(failure.Message, "NotFound");
        AssertEx.Contains(failure.Message, "xe-app-instance-1-net");
        AssertEx.Empty(client.CreatedContainerIds);
    }

    /// <summary>
    ///     <c>none</c> is one of the networks every daemon ships with, and the storage helper runs on it without
    ///     anything having created one. A fake that 404'd on it would refuse the uninstall path.
    /// </summary>
    [Test]
    [Arguments("none")]
    [Arguments("host")]
    [Arguments("bridge")]
    public async Task RunContainer_OnABuiltInNetwork_IsAccepted(string networkName)
    {
        var client = EmptyDaemon();
        client.SeedExistingImage(Image);

        var containerId = await client.RunContainerAsync(Specification() with
        {
            NetworkName = networkName
        });

        AssertEx.NotNullOrEmpty(containerId);
    }

    [Test]
    public async Task RunContainer_UnderANameAlreadyTaken_IsRefusedAsAConflict()
    {
        var client = Client();
        var first = await client.RunContainerAsync(Specification());

        var failure = await AssertEx.ThrowsAsync<DockerRuntimeException>(() => client.RunContainerAsync(Specification()));

        AssertEx.Contains(failure.Message, "Conflict");
        AssertEx.Contains(failure.Message, "xe-app-instance-1-odysseus");
        AssertEx.Equal(expected: 1, client.CreatedContainerIds.Count);

        // The name is free again once the container is gone, which is what lets a rebuild recreate it.
        await client.RemoveContainerAsync(first);
        AssertEx.NotNullOrEmpty(await client.RunContainerAsync(Specification()));
    }

    /// <summary>
    ///     <c>created</c> is the daemon's word for a container that has never been started, and the boot reconciler
    ///     and the state observer render this string verbatim into what the operator reads. Reporting it as
    ///     <c>exited</c> would describe a container that never ran as one that ran and died.
    /// </summary>
    [Test]
    public async Task ListContainersDetailed_ReportsCreatedBeforeTheFirstStartAndExitedAfterAStop()
    {
        var client = Client();
        var containerId = await client.RunContainerAsync(Specification());

        AssertEx.Equal("created", (await client.ListContainersDetailedAsync(Labels()))[0].State);

        await client.StartContainerAsync(containerId);
        AssertEx.Equal("running", (await client.ListContainersDetailedAsync(Labels()))[0].State);

        await client.StopContainerAsync(containerId, TimeSpan.FromSeconds(1));
        AssertEx.Equal("exited", (await client.ListContainersDetailedAsync(Labels()))[0].State);
    }

    /// <summary>
    ///     The one-shot exit code reaches the LIST as well as the inspection. Two reads of one container that
    ///     disagreed about whether it left an exit code behind would let the reconciler render "is exited" where the
    ///     daemon says "exited with exit code 137".
    /// </summary>
    [Test]
    public async Task ListContainersDetailed_ReportsTheOneShotExitCodeInspectAlreadyReports()
    {
        var client = Client();
        client.OneShotExitCode = static _ => 137L;
        var containerId = await client.RunContainerAsync(Specification());
        await client.StartContainerAsync(containerId);

        var summary = (await client.ListContainersDetailedAsync(Labels()))[0];
        var inspection = await client.InspectAsync(containerId);

        AssertEx.Equal("exited", summary.State);
        AssertEx.Equal(expected: 137, summary.ExitCode);
        AssertEx.Equal(expected: 137L, inspection.State.ExitCode);
    }

    [Test]
    public async Task ListContainersDetailed_ReportsANullExitCodeWhenNoneWasReported()
    {
        var client = Client();
        await client.RunContainerAsync(Specification());

        var summary = (await client.ListContainersDetailedAsync(Labels()))[0];

        // Null is "the daemon did not say" and is never read as 0, which would report a crash as a clean exit.
        AssertEx.Null(summary.ExitCode);
    }

    [Test]
    public async Task ListContainersDetailed_FiltersByLabel()
    {
        var client = Client();
        await client.RunContainerAsync(Specification());
        await client.RunContainerAsync(Specification() with
        {
            Name = "someone-elses",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["xe.instance"] = "instance-2",
                ["xe.install"] = "install-2"
            }
        });

        AssertEx.Equal(expected: 1, (await client.ListContainersDetailedAsync(Labels())).Count);
    }

    [Test]
    public async Task ReadLogs_RespectsTheTailAndByteCeilings()
    {
        var client = Client();
        var containerId = await client.RunContainerAsync(Specification());
        for (var line = 0; line < 500; line++)
        {
            client.LogLines.Add("line-" + line.ToString(CultureInfo.InvariantCulture));
        }

        var tailed = await client.ReadLogsAsync(containerId, new ContainerLogRequest
        {
            TailLines = 10
        });
        var bounded = await client.ReadLogsAsync(containerId, new ContainerLogRequest
        {
            TailLines = 500,
            MaxBytes = 64
        });

        AssertEx.Equal(expected: 10, tailed.LineCount);
        AssertEx.False(tailed.Truncated);
        AssertEx.True(bounded.Truncated);
        AssertEx.Equal(expected: 64, Encoding.UTF8.GetByteCount(bounded.Text));
    }

    [Test]
    [Arguments(0, ContainerLogRequest.MinimumTailLines)]
    [Arguments(-5, ContainerLogRequest.MinimumTailLines)]
    [Arguments(50, 50)]
    [Arguments(99999, ContainerLogRequest.MaximumTailLines)]
    public void ReadLogsRequest_ClampsItsCeilingsOnTheWayIn(int asked, int expected)
    {
        // Clamped rather than validated, because a log read is the one runtime call whose cost is set by the
        // container: an application that has been logging for a week can produce gigabytes, so an unbounded request
        // is a way to run the node out of memory through a surface that looks like a diagnostic.
        var request = new ContainerLogRequest
        {
            TailLines = asked,
            MaxBytes = asked
        };

        AssertEx.Equal(expected, request.TailLines);
        AssertEx.Equal(Math.Clamp(asked, ContainerLogRequest.MinimumBytes, ContainerLogRequest.MaximumBytes), request.MaxBytes);
    }

    [Test]
    public async Task ProbeWritablePath_OnAWritableMount_IsTrueAndRecordsTheContainerAndPath()
    {
        var client = Client();
        var containerId = await client.RunContainerAsync(Specification());

        AssertEx.True(await client.ProbeWritablePathAsync(containerId, "/data"));

        // Recorded so a caller's test can prove the probe ran against the mount it was supposed to, and not against
        // some other path that happened to be writable.
        AssertEx.Equal(expected: 1, client.ProbedWritablePaths.Count);
        AssertEx.Equal(containerId, client.ProbedWritablePaths[0].ContainerId);
        AssertEx.Equal("/data", client.ProbedWritablePaths[0].ContainerPath);
    }

    [Test]
    public async Task ProbeWritablePath_WhenTheHookRefuses_IsFalse()
    {
        var client = Client();
        client.WritableProbeOutcome = (_, path) => path != "/data";
        var containerId = await client.RunContainerAsync(Specification());

        // The uid mapping between a container and an engine-created bind mount is a premise, not a construction, and
        // this is the only way to reach the failing half without a daemon whose mode can be arranged on demand.
        AssertEx.False(await client.ProbeWritablePathAsync(containerId, "/data"));
        AssertEx.True(await client.ProbeWritablePathAsync(containerId, "/elsewhere"));
    }

    [Test]
    public async Task ProbeWritablePath_OnAMissingContainer_IsFalse()
    {
        var client = Client();

        AssertEx.False(await client.ProbeWritablePathAsync("fake-container-404", "/data"));
    }

    /// <summary>
    ///     A daemon that already holds <see cref="Image" /> and the instance network. That is the state a create is
    ///     made in — the pipeline pulls and networks first — and the fake refuses a create that has neither, the way
    ///     the daemon 404s. Seeded rather than pulled and created, so the recorded call lists still show only what a
    ///     test itself did.
    /// </summary>
    private static FakeDockerRuntimeClient Client()
    {
        var client = EmptyDaemon();
        client.SeedExistingImage(Image);
        client.SeedExistingNetwork(Network());
        return client;
    }

    /// <summary>A daemon holding nothing, for the tests whose subject is the pull or the network create itself.</summary>
    private static FakeDockerRuntimeClient EmptyDaemon()
    {
        return new FakeDockerRuntimeClient(new DockerDaemonEndpoint(new Uri("unix:///fake-runtime.sock"),
            DockerDaemonEndpointSource.Configuration));
    }

    private static Dictionary<string, string> Labels()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["xe.instance"] = "instance-1",
            ["xe.install"] = "install-1"
        };
    }

    private static ContainerNetworkSpecification Network()
    {
        return new ContainerNetworkSpecification
        {
            Name = "xe-app-instance-1-net",
            Labels = Labels(),
            Internal = false
        };
    }

    private static ContainerSpecification WithPort(int? preferredHostPort)
    {
        return Specification() with
        {
            PublishedPorts =
            [
                new ContainerPortPublication
                {
                    ContainerPort = 8080,
                    HostIp = "127.0.0.1",
                    HostPort = preferredHostPort
                }
            ]
        };
    }

    private static ContainerSpecification Specification()
    {
        return new ContainerSpecification
        {
            Image = Image,
            Name = "xe-app-instance-1-odysseus",
            Labels = Labels(),
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
            Mounts =
            [
                new ContainerMount
                {
                    HostPath = "/var/lib/xe/instances/1/data",
                    ContainerPath = "/data",
                    ReadOnly = false
                }
            ],
            PublishedPorts = [],
            CapabilitiesToDrop = ["ALL"],
            CapabilitiesToAdd = [],
            SecurityOptions = ["no-new-privileges:true"],
            ReadOnlyRootFilesystem = false,
            NetworkName = "xe-app-instance-1-net",
            NetworkAliases = ["odysseus"],
            RestartMode = ContainerRestartMode.UnlessStopped,
            MemoryBytes = 0,
            NanoCpus = 0,
            PidsLimit = 512
        };
    }

    private sealed class RecordingProgress : IProgress<ContainerPullProgress>
    {
        private readonly List<ContainerPullProgress> _reports;

        public RecordingProgress(List<ContainerPullProgress> reports)
        {
            _reports = reports;
        }

        public void Report(ContainerPullProgress value)
        {
            _reports.Add(value);
        }
    }
}
