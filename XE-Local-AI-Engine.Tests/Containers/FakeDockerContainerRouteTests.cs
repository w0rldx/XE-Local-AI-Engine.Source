namespace XE_Local_AI_Engine.Tests.Containers;

using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Testing.FakeDocker;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The production wire client against the fake daemon's container routes — both creation surfaces, because the
///     application path and the Development Mode sandbox path send different subsets of the same wire shape and read
///     different fields back.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class FakeDockerContainerRouteTests
{
    private const string Image = "busybox@sha256:0000000000000000000000000000000000000000000000000000000000000001";

    private const string VolumeImage = "redis@sha256:0000000000000000000000000000000000000000000000000000000000000002";

    /// <summary>
    ///     Replaces <c>RealDaemon_RestartPolicy_ReadsBackAsUnlessStoppedOrNo</c> (the UnlessStopped half).
    ///     <para>
    ///         What this falsifies is the WIRE ROUND TRIP: the request builder, the JSON, and the inspect mapper,
    ///         end to end over a real socket. What it cannot falsify is whether a daemon honoured any of it — the
    ///         fake echoes the create body back, so a builder and a mapper that are wrong in the same way would
    ///         agree with each other here. Enforcement is
    ///         <c>DockerSandboxRealDaemonTests.RealDaemon_CreatedContainer_ReadsBackEveryHardeningGuarantee</c>
    ///         and its neighbours; do not delete those as duplicates of this.
    ///     </para>
    /// </summary>
    [Test]
    public async Task RunContainer_ThenInspect_ReadsBackTheWholeApplicationSpecification()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedImage(Image);
        box.State.SeedNetwork("app-net");

        var containerId = await box.Runtime.RunContainerAsync(Specification());
        var inspection = await box.Runtime.InspectAsync(containerId);

        AssertEx.Equal(containerId, inspection.ContainerId);
        // The daemon reports the name with a leading slash and the mapper trims it; a comparison that failed on a
        // punctuation mark would not be a verification.
        AssertEx.Equal("app-one", inspection.Name);
        AssertEx.Equal(Image, inspection.Image);
        AssertEx.Equal("xe", inspection.Labels["owner"]);
        AssertEx.Equal("1000:1000", inspection.User);
        AssertEx.Equal("app-net", inspection.NetworkMode);
        AssertEx.False(inspection.Privileged, "A container was created privileged.");
        AssertEx.True(inspection.ReadOnlyRootFilesystem, "The read-only root filesystem did not read back.");
        AssertEx.Contains(inspection.CapabilitiesDropped, "ALL");
        AssertEx.Contains(inspection.CapabilitiesAdded, "NET_BIND_SERVICE");
        AssertEx.Contains(inspection.SecurityOptions, "no-new-privileges:true");
        AssertEx.Equal(expected: 0, inspection.DeviceCount);
        AssertEx.Equal("private", inspection.IpcMode);
        AssertEx.Equal(expected: 512L * 1024 * 1024, inspection.MemoryBytes);
        AssertEx.Equal(expected: 1_500_000_000L, inspection.NanoCpus);
        AssertEx.Equal(expected: 256L, inspection.PidsLimit);
        AssertEx.Equal(ContainerRestartMode.UnlessStopped, inspection.RestartMode);
        AssertEx.Contains(inspection.ExtraHosts, "host.docker.internal:host-gateway");
        AssertEx.ContainsSingle(inspection.Mounts,
            mount => string.Equals(mount.Destination, "/data", StringComparison.Ordinal) && !mount.ReadOnly);
        AssertEx.Equal("created", inspection.State.Status);
        AssertEx.False(inspection.State.Running, "A container was running before it was started.");
        AssertEx.Null(inspection.State.StartedAtUtc, "A container that was never started reported a start time.");
    }

    /// <summary>
    ///     The other half of <c>RealDaemon_RestartPolicy_ReadsBackAsUnlessStoppedOrNo</c>. Both halves matter: the
    ///     two modes are one enum on this side and two different daemon words on the other, so a mapping that
    ///     collapsed them would still pass a test that only ever asked for one.
    /// </summary>
    [Test]
    public async Task RunContainer_WithoutARestartPolicy_ReadsBackAsNone()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedImage(Image);
        box.State.SeedNetwork("app-net");

        var containerId = await box.Runtime.RunContainerAsync(Specification() with
        {
            RestartMode = ContainerRestartMode.None
        });

        AssertEx.Equal(ContainerRestartMode.None, (await box.Runtime.InspectAsync(containerId)).RestartMode);
    }

    /// <summary>Replaces <c>RealDaemon_RequestedPortBindings_ArePresentBeforeStart_AndEffectivePortsOnlyAfter</c>.</summary>
    [Test]
    public async Task RequestedPortBindings_ArePresentBeforeStart_AndEffectivePortsOnlyAfter()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedImage(Image);
        box.State.SeedNetwork("app-net");

        var containerId = await box.Runtime.RunContainerAsync(Specification());

        // HostConfig.PortBindings and NetworkSettings.Ports are two different answers and the mapper reads them
        // into two different fields. A fake that served one from the other would pass either assertion alone.
        var beforeStart = await box.Runtime.InspectAsync(containerId);
        AssertEx.ContainsSingle(beforeStart.RequestedPortBindings,
            port => port.ContainerPort == 8080
                    && port.HostPort is null
                    && string.Equals(port.HostIp, "127.0.0.1", StringComparison.Ordinal));
        AssertEx.Empty(beforeStart.PublishedPorts);

        await box.Runtime.StartContainerAsync(containerId);

        var afterStart = await box.Runtime.InspectAsync(containerId);
        AssertEx.ContainsSingle(afterStart.RequestedPortBindings, port => port.ContainerPort == 8080 && port.HostPort is null);
        AssertEx.ContainsSingle(afterStart.PublishedPorts,
            port => port.ContainerPort == 8080
                    && port.HostPort > 0
                    && string.Equals(port.HostIp, "127.0.0.1", StringComparison.Ordinal));
        AssertEx.True(afterStart.State.Running, "A started container did not read back as running.");
        AssertEx.True(afterStart.State.StartedAtUtc is not null, "A started container reported no start time.");
    }

    [Test]
    public async Task AnImageDeclaringAVolume_ReportsTheAnonymousMountInTheEffectiveSet()
    {
        // The effective set is the only place an anonymous volume the image's own VOLUME instruction created shows
        // up, and it is exactly the mount that would put application data outside the instance directory unnoticed.
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedImage(VolumeImage, "/var/lib/redis");
        box.State.SeedNetwork("app-net");

        var containerId = await box.Runtime.RunContainerAsync(Specification() with
        {
            Image = VolumeImage
        });

        var inspection = await box.Runtime.InspectAsync(containerId);

        AssertEx.ContainsSingle(inspection.Mounts,
            mount => string.Equals(mount.Destination, "/var/lib/redis", StringComparison.Ordinal)
                     && string.Equals(mount.Type, "volume", StringComparison.Ordinal),
            "The anonymous volume the image declares is missing from the effective mount set.");
        AssertEx.ContainsSingle(inspection.Mounts, mount => string.Equals(mount.Type, "bind", StringComparison.Ordinal),
            "The requested bind mount was lost when the anonymous volume was synthesized.");
    }

    /// <summary>Replaces <c>RealDaemon_ADigestPinnedImageThatIsAbsent_FailsWithAClassifiedNotFound</c>.</summary>
    [Test]
    public async Task RunContainer_AgainstAnImageTheDaemonDoesNotHave_FailsClassified()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedNetwork("app-net");

        var exception = await AssertEx.ThrowsAsync<DockerRuntimeException>(() => box.Runtime.RunContainerAsync(Specification()));

        // Classified rather than raw: an operator reading this needs an action, and an unclassified transport error
        // names none.
        AssertEx.Equal(DockerDaemonPreflightStatus.ProbeFailed, exception.Status);
        AssertEx.NotNullOrEmpty(exception.Message);
    }

    /// <summary>Replaces <c>RealDaemon_StopContainer_HonoursTheGracePeriodAndIsIdempotent</c>.</summary>
    [Test]
    public async Task StopContainer_SendsTheGracePeriodAndIsIdempotent()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedImage(Image);
        box.State.SeedNetwork("app-net");

        var containerId = await box.Runtime.RunContainerAsync(Specification());
        await box.Runtime.StartContainerAsync(containerId);

        AssertEx.True(await box.Runtime.StopContainerAsync(containerId, TimeSpan.FromSeconds(17)),
            "The first stop of a running container reported that it was already stopped.");
        AssertEx.Equal("17", box.State.LastQueryValue("/stop", "t"),
            "The grace period the caller asked for did not reach the daemon.");

        var stopped = await box.Runtime.InspectAsync(containerId);
        AssertEx.False(stopped.State.Running, "A stopped container still read back as running.");
        AssertEx.Equal("exited", stopped.State.Status);

        // 304 Not Modified, which Docker.DotNet maps to false — "already stopped" is not an error, and a teardown
        // must never have to reason about how far a previous attempt got.
        AssertEx.False(await box.Runtime.StopContainerAsync(containerId, TimeSpan.FromSeconds(17)),
            "The second stop of an already-stopped container reported that it stopped it.");

        await box.Runtime.RemoveContainerAsync(containerId);
        AssertEx.False(await box.Runtime.StopContainerAsync(containerId, TimeSpan.FromSeconds(17)),
            "Stopping a container that no longer exists was not the same answer as stopping a stopped one.");
    }

    [Test]
    public async Task StopContainer_ClampsTheGracePeriodToTenMinutes()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedImage(Image);
        box.State.SeedNetwork("app-net");

        var containerId = await box.Runtime.RunContainerAsync(Specification());
        await box.Runtime.StartContainerAsync(containerId);
        await box.Runtime.StopContainerAsync(containerId, TimeSpan.FromHours(1));

        AssertEx.Equal("600", box.State.LastQueryValue("/stop", "t"));
    }

    /// <summary>
    ///     The parser half of <c>RealDaemon_ListContainersDetailed_ReportsTheStoppedContainerAndItsExitCode</c>,
    ///     which stays in the real-daemon suite as the sentinel for Docker rewording that status prose.
    /// </summary>
    [Test]
    public async Task ListContainersDetailed_ReportsTheStoppedContainerAndItsExitCode()
    {
        // 4.3.3's list response carries no exit code: the client reads it back out of the daemon's own status prose,
        // so the prose is the contract and this is the test that pins the fake to it.
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedImage(Image);
        box.State.SeedNetwork("app-net");

        var containerId = await box.Runtime.RunContainerAsync(Specification());
        await box.Runtime.StartContainerAsync(containerId);
        await box.Runtime.StopContainerAsync(containerId, TimeSpan.FromSeconds(1));
        box.State.Containers[containerId].ExitCode = 137;

        var listed = await box.Runtime.ListContainersDetailedAsync(Labels);

        var summary = AssertEx.NotNull(listed.SingleOrDefault(entry => string.Equals(entry.Id, containerId, StringComparison.Ordinal)),
            "The label filter did not find the stopped container.");
        AssertEx.Equal("exited", summary.State);
        AssertEx.Equal(expected: 137, summary.ExitCode);
        AssertEx.Equal("xe", summary.Labels["owner"]);
    }

    [Test]
    public async Task ListContainersDetailed_ExcludesAContainerWhoseLabelsDoNotMatch()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedImage(Image);
        box.State.SeedNetwork("app-net");

        await box.Runtime.RunContainerAsync(Specification());

        var listed = await box.Runtime.ListContainersDetailedAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["owner"] = "somebody-else"
        });

        AssertEx.Empty(listed, "A label filter that matches nothing returned a container anyway.");
    }

    /// <summary>Replaces <c>RealDaemon_ReadLogs_ReturnsBothStreamsDemultiplexedAndHonoursTheCeilings</c>.</summary>
    [Test]
    public async Task ReadLogs_ReturnsBothStreamsDemultiplexedAndHonoursTheByteCeiling()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedImage(Image);
        box.State.SeedNetwork("app-net");

        var containerId = await box.Runtime.RunContainerAsync(Specification());
        var container = box.State.Containers[containerId];
        container.Logs.Add(new FakeDockerLogFrame(FakeDockerStreamKind.StandardOutput, "listening on 8080\n"));
        container.Logs.Add(new FakeDockerLogFrame(FakeDockerStreamKind.StandardError, "config warning\n"));

        var snapshot = await box.Runtime.ReadLogsAsync(containerId, new ContainerLogRequest());

        // Both halves, in the order the daemon framed them: a reader that dropped the error stream would hide the
        // only thing a failing container usually says.
        AssertEx.Contains(snapshot.Text, "listening on 8080");
        AssertEx.Contains(snapshot.Text, "config warning");
        AssertEx.False(snapshot.Truncated, "A log well under the ceiling was reported as truncated.");
        AssertEx.Equal(expected: 2, snapshot.LineCount);

        // Six of the eight framing bytes are NUL for any realistic chunk size, so their absence is the proof the
        // stream was demultiplexed rather than handed to the caller with its headers still in it.
        AssertEx.False(snapshot.Text.Contains('\0', StringComparison.Ordinal),
            "The log text carries stream-framing bytes, so the multiplexed stream reached the caller undemultiplexed.");

        var bounded = await box.Runtime.ReadLogsAsync(containerId,
            new ContainerLogRequest
            {
                MaxBytes = 1
            });

        AssertEx.True(bounded.Truncated, "A log read against a one-byte ceiling was not reported as truncated.");
        AssertEx.True(bounded.Text.Length <= ContainerLogRequest.MinimumBytes,
            "The byte ceiling did not bound what came back.");
    }

    [Test]
    public async Task RemoveContainer_IsIdempotentAndReleasesTheNetworkEndpoint()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedImage(Image);
        var network = box.State.SeedNetwork("app-net");

        var containerId = await box.Runtime.RunContainerAsync(Specification());
        AssertEx.Contains(network.AttachedContainerIds, containerId);

        await box.Runtime.RemoveContainerAsync(containerId);
        AssertEx.Empty(network.AttachedContainerIds, "Removing the container left its network endpoint behind.");

        // The second removal is a 404 the client swallows, so the fail-closed create path can always clean up after
        // itself without reasoning about how far it got. A throw here fails the test, which is the assertion.
        await box.Runtime.RemoveContainerAsync(containerId);
        AssertEx.Empty(box.State.Containers.Keys, "The container survived the removal that reported success.");
    }

    /// <summary>
    ///     The Development Mode wire shape, which sends a different subset of the same create body and reads it
    ///     back through its own mapper.
    ///     <para>
    ///         Same boundary as the application-surface read-back above: this proves the round trip and cannot
    ///         prove enforcement, because the answer is the echo of the request.
    ///         <c>DockerSandboxRealDaemonTests.RealDaemon_CreatedContainer_ReadsBackEveryHardeningGuarantee</c> is
    ///         the one that asks a real daemon, and it asserts things this cannot — the daemon's own seccomp
    ///         rendering among them. The near-identical names are not duplication; neither replaces the other.
    ///     </para>
    /// </summary>
    [Test]
    public async Task CreateContainer_OnTheSandboxSurface_ReadsBackEveryHardeningGuarantee()
    {
        // The Development Mode path sends a different subset of the same wire shape — tmpfs mounts and no ports,
        // healthcheck or restart policy. It is read back through its own mapper, so it needs its own round trip.
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedImage(Image);

        IDockerRuntimeClient client = box.Runtime;
        var containerId = await client.CreateContainerAsync(new DockerContainerSpecification
        {
            Image = Image,
            Name = "sandbox-one",
            User = "1000:1000",
            WorkingDirectory = "/workspace",
            Entrypoint = ["sleep"],
            Command = ["infinity"],
            NetworkMode = "none",
            CapabilitiesToDrop = ["ALL"],
            SecurityOptions = ["no-new-privileges:true"],
            ReadOnlyRootFilesystem = true,
            TemporaryFilesystems = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/tmp"] = "rw,noexec,nosuid,size=64m"
            },
            BindMounts =
            [
                new DockerBindMount
                {
                    HostPath = "/host/workspace",
                    ContainerPath = "/workspace",
                    ReadOnly = false,
                    Propagation = "private"
                }
            ],
            MemoryBytes = 1024L * 1024 * 1024,
            NanoCpus = 2_000_000_000,
            PidsLimit = 512,
            Labels = Labels
        });

        var settings = await client.InspectContainerAsync(containerId);

        AssertEx.Equal(containerId, settings.ContainerId);
        AssertEx.Equal("1000:1000", settings.User);
        AssertEx.Equal("none", settings.NetworkMode);
        AssertEx.False(settings.Privileged, "The sandbox container was created privileged.");
        AssertEx.True(settings.ReadOnlyRootFilesystem, "The sandbox container's root filesystem is writable.");
        AssertEx.Contains(settings.CapabilitiesDropped, "ALL");
        AssertEx.Empty(settings.CapabilitiesAdded);
        AssertEx.Contains(settings.SecurityOptions, "no-new-privileges:true");
        AssertEx.Equal("rw,noexec,nosuid,size=64m", settings.TemporaryFilesystems["/tmp"]);
        AssertEx.ContainsSingle(settings.Mounts,
            mount => string.Equals(mount.ContainerPath, "/workspace", StringComparison.Ordinal) && !mount.ReadOnly);
        AssertEx.Equal(expected: 512L, settings.PidsLimit);
        AssertEx.Equal(expected: 0, settings.DeviceCount);
        AssertEx.Equal("private", settings.IpcMode);
    }

    [Test]
    public async Task TheSandboxCreationPath_DoesNotReportFieldsOnlyTheApplicationPathSends()
    {
        // The fake echoes the create request back rather than filling anything in, so a container created without a
        // restart policy or extra hosts reports neither — which is what a real daemon does and what makes the two
        // paths distinguishable at all.
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedImage(Image);

        IDockerRuntimeClient client = box.Runtime;
        var containerId = await client.CreateContainerAsync(SandboxSpecification());
        var inspection = await box.Runtime.InspectAsync(containerId);

        AssertEx.Equal(ContainerRestartMode.None, inspection.RestartMode);
        AssertEx.Empty(inspection.ExtraHosts);
        AssertEx.Empty(inspection.RequestedPortBindings);
        AssertEx.Equal(ContainerHealthState.None, inspection.State.Health);
    }

    private static IReadOnlyDictionary<string, string> Labels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["owner"] = "xe"
    };

    private static ContainerSpecification Specification()
    {
        return new ContainerSpecification
        {
            Image = Image,
            Name = "app-one",
            User = "1000:1000",
            Labels = Labels,
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MODE"] = "test"
            },
            Mounts =
            [
                new ContainerMount
                {
                    HostPath = "/host/data",
                    ContainerPath = "/data",
                    ReadOnly = false
                }
            ],
            PublishedPorts =
            [
                new ContainerPortPublication
                {
                    ContainerPort = 8080,
                    HostIp = "127.0.0.1"
                }
            ],
            CapabilitiesToDrop = ["ALL"],
            CapabilitiesToAdd = ["NET_BIND_SERVICE"],
            SecurityOptions = ["no-new-privileges:true"],
            ReadOnlyRootFilesystem = true,
            NetworkName = "app-net",
            NetworkAliases = ["app"],
            RestartMode = ContainerRestartMode.UnlessStopped,
            MemoryBytes = 512L * 1024 * 1024,
            NanoCpus = 1_500_000_000,
            PidsLimit = 256,
            ExtraHosts = ["host.docker.internal:host-gateway"]
        };
    }

    private static DockerContainerSpecification SandboxSpecification()
    {
        return new DockerContainerSpecification
        {
            Image = Image,
            Name = "sandbox-two",
            User = "1000:1000",
            WorkingDirectory = "/workspace",
            Entrypoint = ["sleep"],
            Command = ["infinity"],
            NetworkMode = "none",
            CapabilitiesToDrop = ["ALL"],
            SecurityOptions = ["no-new-privileges:true"],
            ReadOnlyRootFilesystem = true,
            TemporaryFilesystems = new Dictionary<string, string>(StringComparer.Ordinal),
            BindMounts = [],
            MemoryBytes = 0,
            NanoCpus = 0,
            PidsLimit = 512,
            Labels = Labels
        };
    }
}
