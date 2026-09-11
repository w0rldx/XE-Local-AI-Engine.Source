namespace XE_Local_AI_Engine.Tests.Containers;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Containers.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Tests.ContainerSandbox;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The only tests that prove the application-container runtime works against a real Docker Engine.
///     <para>
///         The fake-based suite proves the layer refuses what it should and shapes what it sends. Only this suite
///         proves a real daemon, asked for this exact specification, produces a container whose ports are on
///         loopback, whose effective mounts include the ones its image declared, whose healthcheck reaches a verdict
///         and whose logs come back demultiplexed. Neither half substitutes for the other: a fake cannot show a
///         daemon honours a flag, and a daemon cannot be asked to dishonour one.
///     </para>
///     <para>
///         An unavailable daemon <b>skips with a reason</b> and never passes. Set <c>XE_REQUIRE_DOCKER_TESTS=1</c>
///         where a daemon is promised and the skip becomes a failure, because "these tests did not run" is an
///         environment fact on a laptop and a broken gate on a machine that has Docker.
///     </para>
/// </summary>
public sealed class ContainerRuntimeRealDaemonTests
{
    /// <summary>Set to <c>1</c> where a daemon is promised (CI); an absent one is then a FAILURE, not a skip.</summary>
    private const string RequireDockerVariable = "XE_REQUIRE_DOCKER_TESTS";

    private const int ServedPort = 8080;

    private static readonly TimeSpan DaemonDeadline = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     One probe and one image provisioning for the whole class. Every test needs the same answer, so probing per
    ///     test would multiply the round trips for a result that cannot differ. A skip decided here is rethrown to
    ///     each awaiting test unchanged.
    /// </summary>
    private static readonly Lazy<Task<ContainerRuntimeOptions>> DaemonGate = new(ResolveUsableDaemonAsync);

    [Test]
    public async Task RealDaemon_PullImage_ReportsProgressAndMakesTheImagePresent()
    {
        await using var box = await NewBoxAsync();

        // Not Progress<T>: that posts each callback to the thread pool, so a list read straight after the pull can
        // be missing reports the pull already made and the assertions below would be racing their own evidence.
        var recorder = new ProgressRecorder<ContainerPullProgress>();
        await box.Runtime.PullImageAsync(ContainerRuntimeTestImages.Busybox, recorder);
        var reports = recorder.Reports;

        AssertEx.True(await box.Runtime.ImageExistsAsync(ContainerRuntimeTestImages.Busybox),
            "The pull completed but the digest-pinned image is not present on the daemon.");
        AssertEx.NotEmpty(reports, "The pull reported no progress at all, so nothing would reach an operator waiting on it.");

        // The class gate has already made both images present, and a daemon asked for a digest it already holds
        // short-circuits: it emits its narration and no per-layer lines at all. So the layer COUNTS are what this
        // test can assert about, and they are the assertion that matters — a finished pull must never end with
        // layers outstanding. That is what caught the aggregator counting the daemon's opening narration line as a
        // layer, which made every completed pull report n of n plus one. Folding of the per-layer stream itself is
        // pinned by ContainerRuntimeWireMappingTests against synthetic messages, which can produce layers on demand.
        var last = reports[^1];
        AssertEx.Equal(ContainerRuntimeTestImages.Busybox, last.ImageReference);
        AssertEx.Equal(last.LayerCount, last.CompletedLayers,
            $"The pull returned with layers still outstanding ({last.CompletedLayers} of {last.LayerCount} complete), "
            + "so the final snapshot is not the pull's last state.");
    }

    [Test]
    public async Task RealDaemon_PullImage_WithATagRatherThanADigest_IsRefusedBeforeAnyWireCall()
    {
        // Enforced here and not only by the catalog: a tag lets a different image answer to the same name, and the
        // daemon has no way to tell the caller that what it pulled is not what was reviewed.
        await using var box = await NewBoxAsync();

        await AssertEx.ThrowsAsync<ArgumentException>(() => box.Runtime.PullImageAsync("busybox:1.37", progress: null));
    }

    [Test]
    public async Task RealDaemon_ADigestPinnedImageThatIsAbsent_FailsWithAClassifiedNotFound()
    {
        await using var box = await NewBoxAsync();
        var absent = "busybox@sha256:" + new string('0', count: 64);

        var exception = await AssertEx.ThrowsAsync<DockerRuntimeException>(
            () => box.Runtime.RunContainerAsync(box.Specification(absent)));

        // Classified rather than raw: a create that failed because the image is not there names an operator action,
        // and an unclassified transport error names none.
        AssertEx.NotNullOrEmpty(exception.Message);
    }

    [Test]
    public async Task RealDaemon_CreateNetwork_IsFoundByItsLabelsAndIsIdempotent()
    {
        // Re-entrancy after a crash: the reconciler re-creates a network it may already have created, and the second
        // call must return the same id rather than leave two networks wearing one name.
        await using var box = await NewBoxAsync();

        var first = await box.CreateNetworkAsync();
        var second = await box.Runtime.CreateNetworkAsync(box.NetworkSpecification());

        AssertEx.Equal(first, second);

        var found = await box.Runtime.ListNetworksAsync(box.Labels);
        AssertEx.Equal(expected: 1, found.Count, "The label filter found something other than exactly this test's network.");
        AssertEx.Equal(first, found[0]);
    }

    [Test]
    public async Task RealDaemon_CreateNetwork_OverAForeignNetworkOfTheSameName_ThrowsContainerPolicyException()
    {
        // A name conflict is not proof of ownership. Without this check a network someone else created — carrying the
        // name an installed application happens to use — would silently become the network that application's
        // containers join, which makes the instance and install labels a security input rather than bookkeeping.
        await using var box = await NewBoxAsync();

        var foreignId = await box.CreateForeignNetworkAsync();

        var exception = await AssertEx.ThrowsAsync<ContainerPolicyException>(
            () => box.Runtime.CreateNetworkAsync(box.NetworkSpecification()));

        AssertEx.Equal(ContainerPolicyException.ForeignNetworkReason, exception.Reason);
        AssertEx.Contains(exception.Message, box.NetworkName);

        // Refused, not repaired: removing a network this engine does not own is a worse answer than declining.
        AssertEx.Contains(await box.ListAllNetworkIdsAsync(), foreignId,
            "The foreign network was removed. A refusal must not delete someone else's network.");
    }

    [Test]
    public async Task RealDaemon_RequestedPortBindings_ArePresentBeforeStart_AndEffectivePortsOnlyAfter()
    {
        // The two-pass verification a later slice depends on. Before start the daemon knows what was asked and has
        // assigned nothing; after start it knows both. A verifier reading one member for both questions would either
        // pass a container whose port was never published or fail one that was.
        await using var box = await NewBoxAsync();

        var containerId = await LoopbackPort.BindWithRetryAsync(async candidate =>
            {
                var created = await box.RunAsync(box.SpecificationOnPort(candidate));

                var beforeStart = await box.Runtime.InspectAsync(created);
                AssertEx.Equal(expected: 1, beforeStart.RequestedPortBindings.Count);
                AssertEx.Equal(ServedPort, beforeStart.RequestedPortBindings[0].ContainerPort);
                AssertEx.Equal("127.0.0.1", beforeStart.RequestedPortBindings[0].HostIp);
                AssertEx.Empty(beforeStart.PublishedPorts, "A container that was never started reported a published port.");

                return await box.TryStartAsync(created) ? created : null;
            },
            maxAttempts: 2);

        var afterStart = await box.Runtime.InspectAsync(containerId);

        AssertEx.Equal(expected: 1, afterStart.RequestedPortBindings.Count);
        AssertEx.Equal(expected: 1, afterStart.PublishedPorts.Count);
        AssertEx.Equal(ServedPort, afterStart.PublishedPorts[0].ContainerPort);
    }

    [Test]
    public async Task RealDaemon_PublishedPort_IsAnEphemeralLoopbackPortThatAcceptsAConnection()
    {
        // Recorded is not the same as real. The read-back says the daemon accepted the binding; only a connection
        // says the binding exists, and only checking every entry of both members says nothing landed on 0.0.0.0.
        // The one test that leaves the host port unset, so the daemon's own assignment is what is proven — it carries
        // the same narrow retry, because an unset port is exactly the case RootlessKit can lose a race on.
        await using var box = await NewBoxAsync();
        var containerId = await box.RunAndStartAsync(box.Specification(), bindExplicitHostPort: false);

        var inspection = await box.Runtime.InspectAsync(containerId);

        foreach (var requested in inspection.RequestedPortBindings)
        {
            AssertEx.Equal("127.0.0.1", requested.HostIp);
        }

        AssertEx.NotEmpty(inspection.PublishedPorts, "A started container published nothing, so there is no port to connect to.");
        foreach (var published in inspection.PublishedPorts)
        {
            AssertEx.Equal("127.0.0.1", published.HostIp);
            AssertEx.True(published.HostPort > 1024,
                $"The daemon assigned host port {published.HostPort}, which is not an ephemeral port.");
        }

        var port = inspection.PublishedPorts[0].HostPort;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(10));
        AssertEx.True(client.Connected, $"Nothing accepted a loopback connection on the published port {port}.");
    }

    [Test]
    public async Task RealDaemon_APreferredHostPortThatIsFree_IsHonoured()
    {
        await using var box = await NewBoxAsync();

        // A candidate port is free at the instant it is handed out and nothing reserves it, so the ONE failure this
        // retries is the daemon reporting that address already bound. Everything else propagates.
        var bound = await LoopbackPort.BindWithRetryAsync(async candidate =>
            {
                var created = await box.RunAsync(box.SpecificationOnPort(candidate));
                return await box.TryStartAsync(created) ? new BoundContainer(created, candidate) : null;
            },
            maxAttempts: 2);

        var inspection = await box.Runtime.InspectAsync(bound.ContainerId);
        AssertEx.Equal(bound.HostPort, inspection.PublishedPorts[0].HostPort);
        AssertEx.Equal("127.0.0.1", inspection.PublishedPorts[0].HostIp);
    }

    [Test]
    public async Task RealDaemon_Healthcheck_MovesFromStartingToHealthy()
    {
        await using var box = await NewBoxAsync();

        // Readiness is the test's to grant. The fixture's own healthcheck passes as soon as BusyBox httpd is
        // listening, which can be before the first inspection — so Starting was observed only when this process
        // happened to win a race with the daemon, and the assertion below would sometimes hold nothing. Here the
        // check reads a file on the bind mount that does not exist yet, and the start period is long enough that a
        // failing check keeps the container Starting rather than driving it Unhealthy while the test looks.
        const string SentinelFile = "healthy";
        var containerId = await box.RunAndStartAsync(box.Specification() with
        {
            Entrypoint = ["sh", "-c", "while true; do sleep 1; done"],
            PublishedPorts = [],
            NetworkAliases = [],
            Healthcheck = new ContainerHealthcheck
            {
                Test = ["CMD-SHELL", $"test -f {ContainerBox.WritableMount}/{SentinelFile}"],
                Interval = TimeSpan.FromSeconds(1),
                Timeout = TimeSpan.FromSeconds(2),
                Retries = 3,
                StartPeriod = DaemonDeadline
            }
        });

        var starting = await PollAsync(async () => (await box.Runtime.InspectAsync(containerId)).State.Health,
            static state => state != ContainerHealthState.None,
            DaemonDeadline,
            "the healthcheck to report a state at all");
        AssertEx.Equal(ContainerHealthState.Starting, starting,
            "The container did not report Starting while its healthcheck was failing, so 'not yet healthy' and 'no "
            + "healthcheck at all' would be indistinguishable.");

        await File.WriteAllTextAsync(Path.Combine(box.HostDirectory, SentinelFile), "ready");

        var final = await PollAsync(async () => (await box.Runtime.InspectAsync(containerId)).State.Health,
            static state => state is ContainerHealthState.Healthy or ContainerHealthState.Unhealthy,
            DaemonDeadline,
            "a healthcheck verdict after the sentinel appeared");

        AssertEx.Equal(ContainerHealthState.Healthy, final);
    }

    [Test]
    public async Task RealDaemon_AFailingHealthcheck_ReachesUnhealthy()
    {
        await using var box = await NewBoxAsync();

        var specification = box.Specification() with
        {
            Entrypoint = ["sh", "-c", "while true; do sleep 1; done"],
            PublishedPorts = [],
            Healthcheck = new ContainerHealthcheck
            {
                Test = ["CMD-SHELL", "exit 1"],
                Interval = TimeSpan.FromSeconds(1),
                Timeout = TimeSpan.FromSeconds(2),
                Retries = 2,
                StartPeriod = TimeSpan.FromSeconds(1)
            }
        };

        var containerId = await box.RunAsync(specification);
        await box.StartAsync(containerId);

        var final = await PollAsync(async () => (await box.Runtime.InspectAsync(containerId)).State.Health,
            static state => state == ContainerHealthState.Unhealthy,
            DaemonDeadline,
            "an Unhealthy verdict");

        AssertEx.Equal(ContainerHealthState.Unhealthy, final);
    }

    [Test]
    public async Task RealDaemon_ReadLogs_ReturnsBothStreamsDemultiplexedAndHonoursTheCeilings()
    {
        // The daemon frames a non-TTY log stream with an eight-byte header per chunk, six of whose bytes are NUL for
        // any realistic chunk size. A read that returned those bytes would put binary into an operator-facing log
        // view, and the framing is invisible to a fake.
        await using var box = await NewBoxAsync();

        var specification = box.Specification() with
        {
            Entrypoint = ["sh", "-c", "i=1; while [ $i -le 500 ]; do echo \"out-$i\"; echo \"err-$i\" >&2; i=$((i+1)); done"],
            PublishedPorts = [],
            Healthcheck = null
        };

        var containerId = await box.RunAsync(specification);
        await box.StartAsync(containerId);
        await PollAsync(async () => (await box.Runtime.InspectAsync(containerId)).State.Running,
            static running => !running,
            DaemonDeadline,
            "the log-writing container to exit");

        var whole = await box.Runtime.ReadLogsAsync(containerId, new ContainerLogRequest
        {
            TailLines = 2000
        });

        AssertEx.Contains(whole.Text, "out-500");
        AssertEx.Contains(whole.Text, "err-500");
        AssertEx.False(whole.Text.Contains('\0', StringComparison.Ordinal),
            "The log text carries stream-framing bytes, so the multiplexed stream reached the caller undemultiplexed.");

        var tail = await box.Runtime.ReadLogsAsync(containerId, new ContainerLogRequest
        {
            TailLines = 10
        });
        AssertEx.True(tail.LineCount <= 10, $"A ten-line tail returned {tail.LineCount} lines.");

        var clipped = await box.Runtime.ReadLogsAsync(containerId, new ContainerLogRequest
        {
            TailLines = 2000,
            MaxBytes = 256
        });
        AssertEx.True(clipped.Truncated, "A 256-byte ceiling over a thousand lines did not report truncation.");
    }

    [Test]
    public async Task RealDaemon_StopContainer_HonoursTheGracePeriodAndIsIdempotent()
    {
        await using var box = await NewBoxAsync();
        var containerId = await box.RunFixtureContainerAsync(start: true);

        var stopped = await box.Runtime.StopContainerAsync(containerId, TimeSpan.FromSeconds(5));
        AssertEx.True(stopped, "The first stop of a running container reported that it was already stopped.");

        var inspection = await box.Runtime.InspectAsync(containerId);
        AssertEx.False(inspection.State.Running);
        AssertEx.Equal("exited", inspection.State.Status);

        // False is the daemon's own answer, not an error: a reconciler that stops what it finds must be able to run
        // twice without the second run looking like a failure.
        AssertEx.False(await box.Runtime.StopContainerAsync(containerId, TimeSpan.FromSeconds(5)));
    }

    [Test]
    public async Task RealDaemon_RestartPolicy_ReadsBackAsUnlessStoppedOrNo()
    {
        await using var box = await NewBoxAsync();

        var none = await box.RunAsync(box.Specification() with
        {
            RestartMode = ContainerRestartMode.None,
            PublishedPorts = [],
            Healthcheck = null
        });
        var unlessStopped = await box.RunAsync(box.Specification() with
        {
            Name = box.NextName(),
            RestartMode = ContainerRestartMode.UnlessStopped,
            PublishedPorts = [],
            Healthcheck = null
        });

        AssertEx.Equal(ContainerRestartMode.None, (await box.Runtime.InspectAsync(none)).RestartMode);
        AssertEx.Equal(ContainerRestartMode.UnlessStopped, (await box.Runtime.InspectAsync(unlessStopped)).RestartMode);
    }

    [Test]
    public async Task RealDaemon_ExtraHosts_ReadsBackHostGateway()
    {
        // host-gateway is a daemon-resolved alias rather than an address, so only a real daemon can say whether it
        // was accepted at all.
        await using var box = await NewBoxAsync();

        var containerId = await box.RunAsync(box.Specification() with
        {
            ExtraHosts = ["host.docker.internal:host-gateway"],
            PublishedPorts = [],
            Healthcheck = null
        });

        AssertEx.Contains((await box.Runtime.InspectAsync(containerId)).ExtraHosts, "host.docker.internal:host-gateway");
    }

    [Test]
    public async Task RealDaemon_AnImageDeclaringAVolume_ReportsTheAnonymousMountInTheEffectiveSet()
    {
        // The mount nobody asked for. An image's own VOLUME puts application data outside the instance directory,
        // where a backup of that directory does not include it — so the EFFECTIVE set, not the requested set, is what
        // a later slice compares its plan against.
        await using var box = await NewBoxAsync();

        var volumesBefore = await box.ListVolumeNamesAsync();

        var containerId = await box.RunAsync(box.Specification(ContainerRuntimeTestImages.VolumeDeclaringImage) with
        {
            Entrypoint = ["sh", "-c", "while true; do sleep 1; done"],
            Mounts = [],
            PublishedPorts = [],
            Healthcheck = null,
            ReadOnlyRootFilesystem = false
        });

        var inspection = await box.Runtime.InspectAsync(containerId);

        // The reference the container was created with, not the resolved image id the daemon also reports. A verifier
        // compares this against the digest-pinned reference an application manifest names, and the id matches nothing
        // it holds.
        AssertEx.Equal(ContainerRuntimeTestImages.VolumeDeclaringImage,
            inspection.Image,
            "The inspected image is not the digest-pinned reference the container was created with.");

        AssertEx.Contains(inspection.Mounts,
            static mount => string.Equals(mount.Type, "volume", StringComparison.Ordinal)
                            && string.Equals(mount.Destination, ContainerRuntimeTestImages.VolumeDeclaringImagePath, StringComparison.Ordinal),
            "The effective mount set did not report the anonymous volume the image declares at "
            + $"'{ContainerRuntimeTestImages.VolumeDeclaringImagePath}'. Mounts seen: "
            + string.Join(", ", inspection.Mounts.Select(static mount => $"{mount.Type}:{mount.Destination}")));

        await box.RemoveContainerAsync(containerId);

        // RemoveVolumes = true is what stops a rejected volume outliving the container it belonged to.
        var leaked = (await box.ListVolumeNamesAsync()).Except(volumesBefore, StringComparer.Ordinal).ToArray();
        AssertEx.Empty(leaked,
            "Removing the container left its anonymous volume behind, so a rejected install would leak disk on every retry: "
            + string.Join(", ", leaked));
    }

    [Test]
    public async Task RealDaemon_ProbeWritablePath_IsTrueOnAnEngineCreatedBindMount_AndFalseOnAReadOnlyOneOrAMissingPath()
    {
        await using var box = await NewBoxAsync();

        var containerId = await box.StartProbeContainerAsync(await box.WritingIdentityAsync());

        AssertEx.True(await box.Runtime.ProbeWritablePathAsync(containerId, ContainerBox.WritableMount),
            "The engine-created bind mount is not writable from inside the container, which is the uid-mapping trap this probe exists to detect.");
        AssertEx.False(await box.Runtime.ProbeWritablePathAsync(containerId, ContainerBox.ReadOnlyMount),
            "A read-only mount probed as writable, so the probe is not reading the mount at all.");
        AssertEx.False(await box.Runtime.ProbeWritablePathAsync(containerId, "/nowhere/at/all"),
            "A path that does not exist in the container probed as writable.");
    }

    // Gated on the HOST, not on the daemon: Docker Desktop serves a Linux daemon to a Windows or macOS host, so the
    // daemon gate passes there and the body then asks libc for the engine's effective ids — a DllNotFoundException,
    // not a verdict. The gate is the platform attribute rather than the daemon-unavailable helper on purpose: a
    // non-Linux host is a skip even under XE_REQUIRE_DOCKER_TESTS=1, which promises a daemon and not a kernel.
    [Test]
    [RunOn(OS.Linux)]
    public async Task RealDaemon_OnARootlessDaemon_TheEngineUidMapsToContainerRootForAnEngineCreatedBindMount()
    {
        await using var box = await NewBoxAsync();

        if (!await box.IsRootlessAsync())
        {
            throw new SkipTestException("SKIPPED — this daemon is not rootless, so an in-container uid maps straight through and there is no "
                                        + "mapping to prove here. The rootful counterpart in this class covers that host.");
        }

        // On a rootless daemon the engine's own uid is mapped to 0 inside the container, so root is the id that can
        // write through a directory the engine created — and the engine's own uid, which owns it on the host, cannot.
        var asRoot = await box.StartProbeContainerAsync("0:0");
        AssertEx.True(await box.Runtime.ProbeWritablePathAsync(asRoot, ContainerBox.WritableMount),
            "Container root could not write through an engine-created bind mount on a rootless daemon.");

        var asEngineUid = await box.StartProbeContainerAsync($"{LibC.GetEffectiveUserId()}:{LibC.GetEffectiveGroupId()}");
        AssertEx.False(await box.Runtime.ProbeWritablePathAsync(asEngineUid, ContainerBox.WritableMount),
            "The engine's own uid could write through the mount on a rootless daemon, which is the opposite of the mapping this trap is about.");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealDaemon_OnARootfulDaemon_TheEngineUidWritesAndSoDoesContainerRoot()
    {
        await using var box = await NewBoxAsync();

        if (await box.IsRootlessAsync())
        {
            throw new SkipTestException("SKIPPED — this daemon is rootless, so the identity mapping this test describes does not apply. The "
                                        + "rootless counterpart in this class covers that host.");
        }

        // The counterpart, so one of the pair always runs: CI's runner is rootful and the development box is
        // rootless, and without both halves the mapping is proven on exactly one of them.
        var asEngineUid = await box.StartProbeContainerAsync($"{LibC.GetEffectiveUserId()}:{LibC.GetEffectiveGroupId()}");
        AssertEx.True(await box.Runtime.ProbeWritablePathAsync(asEngineUid, ContainerBox.WritableMount),
            "The engine's own uid could not write through an engine-created bind mount on a rootful daemon.");

        var asRoot = await box.StartProbeContainerAsync("0:0");
        AssertEx.True(await box.Runtime.ProbeWritablePathAsync(asRoot, ContainerBox.WritableMount),
            "Container root could not write through an engine-created bind mount on a rootful daemon.");
    }

    [Test]
    public async Task RealDaemon_NetworkAlias_ResolvesFromASecondContainerOnTheSameNetwork()
    {
        // Service DNS, which every multi-service manifest depends on and which no fake can establish.
        await using var box = await NewBoxAsync();
        await box.RunFixtureContainerAsync(start: true);

        var clientId = await box.RunAsync(box.Specification() with
        {
            Name = box.NextName(),
            Entrypoint = ["sh", "-c", $"wget -q -O /dev/null http://{ContainerBox.ServiceAlias}:{ServedPort}/"],
            Mounts = [],
            PublishedPorts = [],
            Healthcheck = null,
            NetworkAliases = []
        });
        await box.StartAsync(clientId);

        var finished = await PollAsync(async () => (await box.Runtime.InspectAsync(clientId)).State,
            static state => !state.Running,
            DaemonDeadline,
            "the client container to exit");

        AssertEx.Equal(expected: 0L, finished.ExitCode,
            $"The second container could not fetch http://{ContainerBox.ServiceAlias}:{ServedPort}/ over the shared network.");
    }

    [Test]
    public async Task RealDaemon_RemoveNetwork_WhileAttached_Fails_ThenSucceedsAfterRemoval_AndAMissingNetworkIsNotAnError()
    {
        // Teardown ordering, made observable. A reconciler that removed the network first would leave a container
        // attached to nothing and no error to say so.
        await using var box = await NewBoxAsync();
        var containerId = await box.RunFixtureContainerAsync(start: true);

        await AssertEx.ThrowsAsync<DockerRuntimeException>(() => box.Runtime.RemoveNetworkAsync(box.NetworkName));

        await box.Runtime.StopContainerAsync(containerId, TimeSpan.FromSeconds(5));
        await box.RemoveContainerAsync(containerId);
        await box.Runtime.RemoveNetworkAsync(box.NetworkName);
        box.ForgetNetwork();

        AssertEx.Empty(await box.Runtime.ListNetworksAsync(box.Labels), "The network survived its own removal.");

        // Idempotent: a missing network is the state the caller wanted, so removing it again is not a failure.
        await box.Runtime.RemoveNetworkAsync(box.NetworkName);
    }

    [Test]
    public async Task RealDaemon_ListContainersDetailed_ReportsTheStoppedContainerAndItsExitCode()
    {
        // The assertion the fake cannot make. A container stopped outside this engine stays listed as `exited`, and
        // its exit code is parsed out of the daemon's own status prose — so if Docker ever rewords that string, this
        // test is what says so before an operator sees a stopped application reported as running.
        await using var box = await NewBoxAsync();

        var running = await box.RunFixtureContainerAsync(start: true);
        var exitingId = await box.RunAsync(box.Specification() with
        {
            Name = box.NextName(),
            Entrypoint = ["sh", "-c", "exit 7"],
            Mounts = [],
            PublishedPorts = [],
            Healthcheck = null,
            NetworkAliases = []
        });
        await box.StartAsync(exitingId);

        await PollAsync(async () => (await box.Runtime.InspectAsync(exitingId)).State.Running,
            static isRunning => !isRunning,
            DaemonDeadline,
            "the exiting container to stop");

        var listed = await box.Runtime.ListContainersDetailedAsync(box.Labels);

        AssertEx.Contains(listed, summary => summary.Id == running && string.Equals(summary.State, "running", StringComparison.Ordinal));

        var exited = AssertEx.NotNull(listed.FirstOrDefault(summary => summary.Id == exitingId),
            "A stopped container was not listed, so an application stopped outside XE would look uninstalled.");
        AssertEx.Equal("exited", exited.State);
        AssertEx.Equal(expected: 7, exited.ExitCode);
    }

    [Test]
    public async Task RealDaemon_ListContainersDetailed_RefusesAnEmptyLabelFilter()
    {
        // An empty filter lists every container on the daemon, including ones this engine did not create.
        await using var box = await NewBoxAsync();

        await AssertEx.ThrowsAsync<ArgumentException>(
            () => box.Runtime.ListContainersDetailedAsync(new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    /// <summary>
    ///     Poll a real daemon to a deadline. Used only where the thing being waited for runs on the daemon's clock —
    ///     a healthcheck interval, a container exiting — and there is no gate this process can hold.
    /// </summary>
    private static async Task<T> PollAsync<T>(Func<Task<T>> read, Func<T, bool> reached, TimeSpan timeout, string what)
    {
        // real-timer: the daemon decides when a healthcheck runs and when a process exits. A FakeTimeProvider moves
        // this test's clock and nothing else, so there is nothing here that could be faked.
        using var deadline = new CancellationTokenSource(timeout);
        T last;

        while (true)
        {
            last = await read().ConfigureAwait(false);
            if (reached(last))
            {
                return last;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        throw new InvalidOperationException(
            $"Waited {timeout.TotalSeconds:0} s for {what} and it never happened. Last observation: {last}.");
    }

    private static async Task<ContainerBox> NewBoxAsync()
    {
        return new ContainerBox(await RequireDaemonAsync());
    }

    private static async Task<ContainerRuntimeOptions> RequireDaemonAsync()
    {
        return await DaemonGate.Value.ConfigureAwait(false);
    }

    private static async Task<ContainerRuntimeOptions> ResolveUsableDaemonAsync()
    {
        var attempts = new List<string>();

        foreach (var candidate in DaemonCandidates())
        {
            var endpoint = DockerDaemonEndpointResolver.Resolve(candidate.DaemonEndpoint);
            DockerDaemonIdentity identity;

            await using (var client = ContainerBox.CreateRuntime(candidate))
            {
                try
                {
                    identity = await client.ProbeAsync();
                }
                catch (DockerRuntimeException exception)
                {
                    attempts.Add($"{exception.Status} at '{endpoint.Display}' (found via {endpoint.Source}): {exception.Message}");
                    continue;
                }
            }

            if (!identity.OperatingSystem.Equals("linux", StringComparison.OrdinalIgnoreCase))
            {
                throw Unavailable($"the reachable Docker daemon at '{endpoint.Display}' runs '{identity.OperatingSystem}' containers, not Linux. "
                                  + "Bind storage and loopback port publishing mean something different there and cannot be verified.");
            }

            await EnsureImagesAsync(candidate);
            return candidate;
        }

        throw Unavailable("no usable Docker daemon. Tried " + string.Join(" | ", attempts)
                          + " Start Docker, or point DOCKER_HOST at a daemon this user can open, and re-run.");
    }

    /// <summary>
    ///     The endpoints to try, each named by the PRODUCTION resolver. The second candidate exists because the
    ///     resolver deliberately stops at an existing <c>/var/run/docker.sock</c> and never falls through to a
    ///     per-user socket — correct for a product whose operator attests to one daemon, and the reason every test
    ///     here would otherwise skip on a box that has a root-owned system socket and a rootless daemon of its own.
    /// </summary>
    private static IEnumerable<ContainerRuntimeOptions> DaemonCandidates()
    {
        var options = new ContainerRuntimeOptions();
        yield return options;

        if (OperatingSystem.IsWindows())
        {
            yield break;
        }

        if (DockerDaemonEndpointResolver.Resolve(options.DaemonEndpoint).Source != DockerDaemonEndpointSource.DefaultUnixSocket)
        {
            yield break;
        }

        var runtimeDirectory = Environment.GetEnvironmentVariable(DockerDaemonEndpointResolver.UserRuntimeDirectoryVariable);
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            yield break;
        }

        var userSocket = Path.Combine(runtimeDirectory, "docker.sock");
        if (!File.Exists(userSocket))
        {
            yield break;
        }

        yield return options with
        {
            DaemonEndpoint = "unix://" + userSocket
        };
    }

    /// <summary>
    ///     Make both pinned images present, through the PRODUCTION pull. CI pre-pulls them as their own steps so a
    ///     registry blip fails there as infrastructure; this is what keeps a fresh laptop from skipping the suite.
    /// </summary>
    private static async Task EnsureImagesAsync(ContainerRuntimeOptions options)
    {
        await using var runtime = ContainerBox.CreateRuntime(options);

        foreach (var image in new[] { ContainerRuntimeTestImages.Busybox, ContainerRuntimeTestImages.VolumeDeclaringImage })
        {
            if (await runtime.ImageExistsAsync(image))
            {
                continue;
            }

            try
            {
                await runtime.PullImageAsync(image, progress: null);
            }
            catch (DockerRuntimeException exception)
            {
                throw Unavailable($"the pinned test image '{image}' is not on this daemon and pulling it failed ({exception.Message}). "
                                  + $"Run `docker pull {image}` and re-run.");
            }
        }
    }

    /// <summary>
    ///     How a missing prerequisite is reported: a skip that names what was missing, or — under
    ///     <c>XE_REQUIRE_DOCKER_TESTS=1</c> — a failure. Both carry the same reason, so the output reads the same
    ///     either way and only the verdict changes.
    /// </summary>
    private static Exception Unavailable(string reason)
    {
        var message = reason + " These are the ONLY tests that prove the application-container runtime works against a real daemon; "
                             + "a green run without them is not evidence that an installed application would start.";

        return string.Equals(Environment.GetEnvironmentVariable(RequireDockerVariable), "1", StringComparison.Ordinal)
            ? new InvalidOperationException($"REQUIRED — {RequireDockerVariable}=1, so this is a failure rather than a skip: {message}")
            : new SkipTestException($"SKIPPED — {message}");
    }

    /// <summary>One container that started, together with the host port it was asked to bind.</summary>
    private sealed record BoundContainer(string ContainerId, int HostPort);

    /// <summary>
    ///     A synchronous <see cref="IProgress{T}" />. <see cref="Progress{T}" /> posts each callback to the
    ///     synchronization context or the thread pool, so reports made during an operation can still be in flight
    ///     when the caller reads them the instant it returns — which turns an assertion about what was reported into
    ///     a race with the scheduler.
    /// </summary>
    private sealed class ProgressRecorder<T> : IProgress<T>
    {
        private readonly ConcurrentQueue<T> _reports = new();

        public IReadOnlyList<T> Reports => [.. _reports];

        public void Report(T value)
        {
            _reports.Enqueue(value);
        }
    }

    /// <summary>The engine process's own effective ids, which is what a bind mount it creates is owned by.</summary>
    private static class LibC
    {
        [DllImport("libc", EntryPoint = "geteuid")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        public static extern uint GetEffectiveUserId();

        [DllImport("libc", EntryPoint = "getegid")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        public static extern uint GetEffectiveGroupId();
    }

    /// <summary>
    ///     One test's containers, network and host directory, all labelled with a run id so nothing this suite creates
    ///     can be confused with — or clean up — anything else on a shared developer daemon.
    ///     <para>
    ///         Disposal is best effort by design: a test that already failed must not have its verdict replaced by a
    ///         cleanup error. The teardown ORDER, which a later slice has to follow, is asserted explicitly by
    ///         <c>RealDaemon_RemoveNetwork_WhileAttached_Fails_ThenSucceedsAfterRemoval_AndAMissingNetworkIsNotAnError</c>.
    ///     </para>
    /// </summary>
    private sealed class ContainerBox : IAsyncDisposable
    {
        /// <summary>The alias the fixture service answers to on the shared network.</summary>
        public const string ServiceAlias = "app";

        /// <summary>The container path the engine-created bind mount is attached at.</summary>
        public const string WritableMount = "/data";

        /// <summary>The same host directory, mounted a second time read-only, so one fixture proves both directions.</summary>
        public const string ReadOnlyMount = "/readonly";

        private readonly List<string> _containers = [];
        private readonly List<string> _foreignNetworks = [];
        private readonly DockerDaemonEndpoint _endpoint;
        private readonly ContainerRuntimeOptions _options;
        private int _nameCounter;
        private string? _networkId;

        public ContainerBox(ContainerRuntimeOptions options)
        {
            _options = options;
            _endpoint = DockerDaemonEndpointResolver.Resolve(options.DaemonEndpoint);
            Runtime = CreateRuntime(options);
            RunId = Guid.NewGuid().ToString("N")[..12];
            NetworkName = "xe-crt-" + RunId;
            HostDirectory = Path.Combine(Path.GetTempPath(), "xe-container-runtime-tests", RunId);
            Directory.CreateDirectory(HostDirectory);
            File.WriteAllText(Path.Combine(HostDirectory, "index.html"), "<html><body>xe</body></html>");

            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["xe.test.suite"] = "container-runtime",
                ["xe.test.run"] = RunId
            };
        }

        public string HostDirectory { get; }

        public IReadOnlyDictionary<string, string> Labels { get; }

        public string NetworkName { get; }

        public IContainerRuntime Runtime { get; }

        public string RunId { get; }

        /// <summary>A runtime client built exactly the way the composition root builds one.</summary>
        public static IContainerRuntime CreateRuntime(ContainerRuntimeOptions options)
        {
            return new DockerContainerRuntimeFactory(new StaticOptionsMonitor<ContainerRuntimeOptions>(options),
                    NullLoggerFactory.Instance)
                .CreateRuntime(DockerDaemonEndpointResolver.Resolve(options.DaemonEndpoint));
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var containerId in _containers)
            {
                try
                {
                    await Runtime.RemoveContainerAsync(containerId);
                }
                catch (DockerRuntimeException)
                {
                    // Already gone, or the test removed it itself. Cleanup must not replace a test's verdict.
                }
            }

            if (_networkId is not null)
            {
                try
                {
                    await Runtime.RemoveNetworkAsync(NetworkName);
                }
                catch (DockerRuntimeException)
                {
                    // As above.
                }
            }

            using (var raw = RawClient())
            {
                foreach (var networkId in _foreignNetworks)
                {
                    try
                    {
                        await raw.Networks.DeleteNetworkAsync(networkId);
                    }
                    catch (DockerApiException)
                    {
                        // As above.
                    }
                }
            }

            await Runtime.DisposeAsync();

            if (Directory.Exists(HostDirectory))
            {
                try
                {
                    Directory.Delete(HostDirectory, recursive: true);
                }
                catch (UnauthorizedAccessException)
                {
                    // A rootful daemon can leave root-owned files in an engine-created directory. Leaving a temp
                    // directory behind is a smaller problem than failing every test in the class because of it.
                }
                catch (IOException)
                {
                    // As above.
                }
            }
        }

        public ContainerNetworkSpecification NetworkSpecification()
        {
            return new ContainerNetworkSpecification
            {
                Name = NetworkName,
                Labels = Labels,
                Internal = false
            };
        }

        public async Task<string> CreateNetworkAsync()
        {
            _networkId ??= await Runtime.CreateNetworkAsync(NetworkSpecification());
            return _networkId;
        }

        /// <summary>
        ///     A bridge network with this test's name and NONE of its labels, created through the raw client so the
        ///     runtime under test cannot have been the thing that made it.
        /// </summary>
        public async Task<string> CreateForeignNetworkAsync()
        {
            using var raw = RawClient();
            var created = await raw.Networks.CreateNetworkAsync(new NetworksCreateParameters
            {
                Name = NetworkName,
                Driver = "bridge"
            });

            _foreignNetworks.Add(created.ID);
            return created.ID;
        }

        public async Task<IReadOnlyList<string>> ListAllNetworkIdsAsync()
        {
            using var raw = RawClient();
            return [.. (await raw.Networks.ListNetworksAsync()).Select(static network => network.ID)];
        }

        public async Task<IReadOnlyList<string>> ListVolumeNamesAsync()
        {
            using var raw = RawClient();
            return [.. (await raw.Volumes.ListAsync(CancellationToken.None)).Volumes.Select(static volume => volume.Name)];
        }

        public string NextName()
        {
            return $"xe-crt-{RunId}-{++_nameCounter}";
        }

        /// <summary>The fixture specification: a BusyBox web server on the shared network, serving the bind mount.</summary>
        public ContainerSpecification Specification(string image = ContainerRuntimeTestImages.Busybox)
        {
            return new ContainerSpecification
            {
                Image = image,
                Name = NextName(),
                User = null,
                Labels = Labels,
                Environment = new Dictionary<string, string>(StringComparer.Ordinal),
                Mounts =
                [
                    new ContainerMount
                    {
                        HostPath = HostDirectory,
                        ContainerPath = WritableMount,
                        ReadOnly = false
                    }
                ],
                PublishedPorts =
                [
                    new ContainerPortPublication
                    {
                        ContainerPort = ServedPort,
                        HostIp = "127.0.0.1"
                    }
                ],
                CapabilitiesToDrop = ["ALL"],
                CapabilitiesToAdd = [],
                SecurityOptions = ["no-new-privileges:true", DockerSeccompProfile.SecurityOption],
                ReadOnlyRootFilesystem = true,
                NetworkName = NetworkName,
                NetworkAliases = [ServiceAlias],
                RestartMode = ContainerRestartMode.None,
                MemoryBytes = 0,
                NanoCpus = 0,
                PidsLimit = 256,
                Entrypoint = ["httpd", "-f", "-p", ServedPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "-h", WritableMount],
                Healthcheck = new ContainerHealthcheck
                {
                    Test = ["CMD-SHELL", $"wget -q -O /dev/null http://127.0.0.1:{ServedPort}/ || exit 1"],
                    Interval = TimeSpan.FromSeconds(1),
                    Timeout = TimeSpan.FromSeconds(2),
                    Retries = 10,
                    StartPeriod = TimeSpan.FromSeconds(1)
                }
            };
        }

        public async Task<string> RunAsync(ContainerSpecification specification)
        {
            await CreateNetworkAsync();
            var containerId = await Runtime.RunContainerAsync(specification);
            _containers.Add(containerId);
            return containerId;
        }

        public async Task<string> RunFixtureContainerAsync(bool start)
        {
            return start
                ? await RunAndStartAsync(Specification())
                : await RunAsync(Specification());
        }

        /// <summary>The fixture specification with its single published port pinned to <paramref name="hostPort" />.</summary>
        public ContainerSpecification SpecificationOnPort(int hostPort)
        {
            return WithHostPort(Specification(), hostPort);
        }

        /// <summary>
        ///     Create and start one container, retrying once — and only — on the daemon's port-bind collision.
        ///     <para>
        ///         Rootless Docker publishes a host port through RootlessKit, which binds it in a separate step from
        ///         the one that chose it. On a box several checkouts share, a port free at the choice can be taken by
        ///         the bind ("RootlessKit PortManager.AddPort(): … bind: address already in use"), and a suite that
        ///         creates a dozen containers meets it eventually. That is the machine, not this engine, so it is
        ///         retried once with a fresh candidate and the failed container is removed first. Every other start
        ///         failure propagates unchanged: a retried defect is a defect that passes.
        ///     </para>
        /// </summary>
        public Task<string> RunAndStartAsync(ContainerSpecification specification, bool bindExplicitHostPort = true)
        {
            ArgumentNullException.ThrowIfNull(specification);

            return LoopbackPort.BindWithRetryAsync(async candidate =>
                {
                    var containerId = await RunAsync(bindExplicitHostPort ? WithHostPort(specification, candidate) : specification);
                    return await TryStartAsync(containerId) ? containerId : null;
                },
                maxAttempts: 2);
        }

        /// <summary>
        ///     Start a container, answering false — with the container removed — only on that one bind collision.
        ///     Any other failure is thrown, because it is the thing the test was there to find.
        /// </summary>
        public async Task<bool> TryStartAsync(string containerId)
        {
            try
            {
                await StartAsync(containerId);
                return true;
            }
            catch (DockerRuntimeException exception) when (IsHostPortAlreadyBound(exception))
            {
                await RemoveContainerAsync(containerId);
                return false;
            }
        }

        /// <summary>A long-lived container carrying both mounts, so the write probe has something to probe.</summary>
        public async Task<string> StartProbeContainerAsync(string user)
        {
            var containerId = await RunAsync(Specification() with
            {
                Name = NextName(),
                User = user,
                Entrypoint = ["sh", "-c", "while true; do sleep 1; done"],
                PublishedPorts = [],
                Healthcheck = null,
                NetworkAliases = [],
                ReadOnlyRootFilesystem = false,
                Mounts =
                [
                    new ContainerMount
                    {
                        HostPath = HostDirectory,
                        ContainerPath = WritableMount,
                        ReadOnly = false
                    },
                    new ContainerMount
                    {
                        HostPath = HostDirectory,
                        ContainerPath = ReadOnlyMount,
                        ReadOnly = true
                    }
                ]
            });

            await StartAsync(containerId);
            return containerId;
        }

        public Task StartAsync(string containerId)
        {
            return Runtime.StartContainerAsync(containerId);
        }

        public async Task RemoveContainerAsync(string containerId)
        {
            await Runtime.RemoveContainerAsync(containerId);
            _containers.Remove(containerId);
        }

        /// <summary>Stop tracking the network, after a test removed it itself.</summary>
        public void ForgetNetwork()
        {
            _networkId = null;
        }

        public async Task<bool> IsRootlessAsync()
        {
            return (await Runtime.ProbeAsync()).IsRootless;
        }

        /// <summary>
        ///     The in-container identity that can write through an engine-created bind mount ON THIS DAEMON. Left to
        ///     the daemon rather than pinned, because which id that is IS the mapping: root on a rootless daemon, the
        ///     engine's own ids on a rootful one.
        /// </summary>
        public async Task<string> WritingIdentityAsync()
        {
            // A non-Linux host has no libc to ask and no uid to map: Docker Desktop presents an engine-created bind
            // mount through its own file-sharing layer, where container root writes regardless. Answering "0:0" there
            // keeps the tests that only need SOME writing identity running on Windows and macOS, instead of ending in
            // a DllNotFoundException from geteuid. The identity MAPPING itself is a Linux claim and is gated as one.
            return !OperatingSystem.IsLinux() || await IsRootlessAsync()
                ? "0:0"
                : $"{LibC.GetEffectiveUserId()}:{LibC.GetEffectiveGroupId()}";
        }

        /// <summary>
        ///     Whether the daemon refused the start because the host port was already bound. Matched on the bind
        ///     error the kernel produced, which is nested inside the daemon's API exception; the classified wrapper
        ///     says only that the request was rejected.
        /// </summary>
        private static bool IsHostPortAlreadyBound(Exception exception)
        {
            for (var current = (Exception?)exception; current is not null; current = current.InnerException)
            {
                if (current.Message.Contains("bind: address already in use", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The specification with every host port it leaves unset pinned to <paramref name="hostPort" />.</summary>
        private static ContainerSpecification WithHostPort(ContainerSpecification specification, int hostPort)
        {
            return specification with
            {
                PublishedPorts =
                [
                    .. specification.PublishedPorts.Select(publication => publication with
                    {
                        HostPort = publication.HostPort ?? hostPort
                    })
                ]
            };
        }

        private DockerClient RawClient()
        {
            return new DockerClientBuilder().WithEndpoint(_endpoint.Uri)
                                            .WithTimeout(TimeSpan.FromSeconds(_options.DaemonProbeTimeoutSeconds))
                                            .Build();
        }
    }
}
