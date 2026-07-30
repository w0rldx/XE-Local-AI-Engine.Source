namespace XE_Local_AI_Engine.Tests.ContainerSandbox;

using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Real-daemon integration coverage for the §3.8 hardening contract (plan decision D4).
///     <para>
///         The unit suite proves the provider <em>refuses</em> a container whose settings did not take. Only this
///         suite proves the settings take at all — that a real Docker Engine, asked for this exact specification,
///         produces a container that is genuinely non-root, capability-stripped, read-only-rooted and network-isolated.
///         Neither half substitutes for the other: fakes cannot show a daemon honours a flag, and a real daemon cannot
///         be made to dishonour one on request.
///     </para>
///     <para>
///         D4's rule, implemented literally: an unavailable daemon <b>skips with a reason</b> and never passes. A suite
///         that went green because it quietly skipped the only tests exercising isolation would be worse than a red
///         one, so every skip below names what was missing and how to supply it.
///     </para>
/// </summary>
public sealed class DockerSandboxRealDaemonTests
{
    /// <summary>
    ///     The image these tests create containers from. Small, and it carries the <c>sh</c>, <c>id</c>, <c>cat</c> and
    ///     <c>touch</c> the probes below need. Digest-pinned because the provider refuses anything else.
    /// </summary>
    private const string TestImage = "alpine@sha256:14358309a308569c32bdc37e2e0e9694be33a9d99e68afb0f5ff33cc1f695dce";

    private static readonly DateTimeOffset FixedNow = new(year: 2026, month: 7, day: 29, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    [Test]
    public async Task RealDaemon_Preflight_ReachesTheDaemonAndPinsIt()
    {
        var options = await RequireDaemonAsync();
        using var attestationRoot = new TemporaryDirectory();

        using var store = new DockerDaemonAttestationStore(new FixedNodeDataDirectory(attestationRoot.Path),
            NullLogger<DockerDaemonAttestationStore>.Instance);
        var service = new DockerDaemonPreflightService(new StaticOptionsMonitor<ContainerSandboxOptions>(options),
            new DockerDotNetRuntimeClientFactory(new StaticOptionsMonitor<ContainerSandboxOptions>(options)),
            store,
            new FixedTimeProvider(FixedNow),
            NullLogger<DockerDaemonPreflightService>.Instance);

        var preflight = await service.InspectAsync();

        AssertEx.Equal(DockerDaemonPreflightStatus.Ready, preflight.Status);
        var observed = AssertEx.NotNull(preflight.ObservedDaemon);
        AssertEx.NotNullOrEmpty(observed.DaemonId);
        AssertEx.NotNullOrEmpty(observed.ServerVersion);
        AssertEx.Equal(AssertEx.NotNull(preflight.PinnedDaemon).DaemonId, observed.DaemonId);
    }

    [Test]
    public async Task RealDaemon_CreatedContainer_ReadsBackEveryHardeningGuarantee()
    {
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateAsync(options);

        // Read back off the running container through the same inspect path the provider verifies against. "We passed
        // the flag" is not verification; this is what the flag became.
        var settings = await fixture.Client.InspectContainerAsync(fixture.ContainerId);

        AssertEx.Equal($"{options.UserId}:{options.GroupId}", settings.User);
        AssertEx.False(settings.Privileged);
        AssertEx.True(settings.ReadOnlyRootFilesystem);
        AssertEx.Contains(settings.CapabilitiesDropped, "ALL");
        AssertEx.Empty(settings.CapabilitiesAdded);
        AssertEx.Contains(settings.SecurityOptions, option => option.Contains("no-new-privileges", StringComparison.OrdinalIgnoreCase));
        AssertEx.Equal("none", settings.NetworkMode);
        AssertEx.Equal(expected: 0, settings.DeviceCount);
        AssertEx.NotEqual("host", settings.PidMode);
        AssertEx.NotEqual("host", settings.IpcMode);
        AssertEx.NotEqual("host", settings.UtsMode);
        AssertEx.Contains(settings.TemporaryFilesystems[options.ScratchMountTarget], "size=");
        AssertEx.Equal((long)options.MemoryMb * 1024 * 1024, settings.MemoryBytes);
        AssertEx.Equal((long)(options.CpuCount * 1_000_000_000d), settings.NanoCpus);
        AssertEx.Equal((long)options.PidsLimit, settings.PidsLimit);
        AssertEx.ContainsSingle(settings.Mounts,
            mount => string.Equals(mount.ContainerPath, options.WorkspaceMountTarget, StringComparison.Ordinal)
                     && string.Equals(mount.Propagation, "private", StringComparison.Ordinal));

        // And the verifier agrees, which is the assertion the provider actually makes.
        AssertEx.Empty(DockerSandboxHardening.FindViolations(fixture.Specification, settings));
    }

    [Test]
    public async Task RealDaemon_InsideTheContainer_TheGuaranteesActuallyHold()
    {
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateAsync(options);

        // The inspect read-back says what the daemon recorded. These say what the kernel did — a stronger claim, and
        // the one the operator's threat model rests on.
        AssertEx.Equal("1000", await ProbeAsync(fixture, "id -u"));
        AssertEx.Equal("1000", await ProbeAsync(fixture, "id -g"));
        AssertEx.Equal("READONLY", await ProbeAsync(fixture, "touch /probe 2>/dev/null && echo WRITABLE || echo READONLY"));
        AssertEx.Equal("SCRATCH-WRITABLE", await ProbeAsync(fixture, $"touch {options.ScratchMountTarget}/probe && echo SCRATCH-WRITABLE"));
        AssertEx.Equal("NET-BLOCKED", await ProbeAsync(fixture, "wget -T2 -q -O- http://1.1.1.1 >/dev/null 2>&1 && echo NET-OK || echo NET-BLOCKED"));
        AssertEx.Equal("NoNewPrivs:\t1", await ProbeAsync(fixture, "grep NoNewPrivs /proc/self/status"));

        // Every capability gone, not merely "ALL was requested": an all-zero effective and bounding set is what a
        // successful --cap-drop ALL looks like from inside.
        AssertEx.Equal("CapEff:\t0000000000000000", await ProbeAsync(fixture, "grep CapEff /proc/self/status"));
        AssertEx.Equal("CapBnd:\t0000000000000000", await ProbeAsync(fixture, "grep CapBnd /proc/self/status"));
    }

    [Test]
    public async Task RealDaemon_TheWorkspaceMountIsVisibleInsideTheContainer()
    {
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateAsync(options);
        await File.WriteAllTextAsync(Path.Combine(fixture.WorkspaceRoot, "marker.txt"), "from-host");

        var handle = fixture.Handle;
        // The sandbox path is "/marker.txt", not "/workspace/marker.txt": the sandbox root IS the workspace, exactly
        // as it is for the process provider.
        var content = await fixture.Provider.ReadFileAsync(handle, "/marker.txt", maxBytes: 4096);

        AssertEx.Equal("from-host", content.Trim());
    }

    [Test]
    public async Task RealDaemon_CopyOut_LandsTheFileOnTheHost()
    {
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateAsync(options);
        await File.WriteAllTextAsync(Path.Combine(fixture.WorkspaceRoot, "export.txt"), "exported");
        var destination = Path.Combine(fixture.WorkspaceRoot, "..", "copied-out.txt");

        await fixture.Provider.CopyOutAsync(fixture.Handle,
            new SandboxCopyRequest { SourcePath = "/export.txt", DestinationPath = destination });

        AssertEx.Equal("exported", (await File.ReadAllTextAsync(destination)).Trim());
    }


    [Test]
    public async Task RealDaemon_TheWorkingDirectoryIsMappedOntoTheWorkspaceRatherThanTheContainerRoot()
    {
        // Development Mode passes the literal "/" for EVERY command. Unmapped, that reaches the wire as WorkingDir="/"
        // and every build, test and git command runs in the container's root — where there is no repository. The
        // second assertion is the one that cannot be faked: a RELATIVE write from that working directory has to land
        // in the engine's own workspace directory on the host.
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateAsync(options);

        var pwd = await fixture.Provider.ExecuteAsync(fixture.Handle,
            new SandboxCommandRequest
            {
                ExecutionId = Guid.NewGuid().ToString("N"),
                Executable = "/bin/sh",
                Arguments = ["-c", "pwd && printf relative-write > from-cwd.txt"],
                WorkingDirectory = "/"
            });

        AssertEx.Equal(options.WorkspaceMountTarget, pwd.StandardOutput.Trim());
        AssertEx.Equal("relative-write", await File.ReadAllTextAsync(Path.Combine(fixture.WorkspaceRoot, "from-cwd.txt")));
    }


    [Test]
    public async Task RealDaemon_ANestedWorkingDirectoryIsMappedUnderTheWorkspaceToo()
    {
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateAsync(options);
        Directory.CreateDirectory(Path.Combine(fixture.WorkspaceRoot, "nested"));

        var pwd = await fixture.Provider.ExecuteAsync(fixture.Handle,
            new SandboxCommandRequest
            {
                ExecutionId = Guid.NewGuid().ToString("N"),
                Executable = "/bin/sh",
                Arguments = ["-c", "pwd"],
                WorkingDirectory = "/nested"
            });

        AssertEx.Equal(options.WorkspaceMountTarget + "/nested", pwd.StandardOutput.Trim());
    }


    [Test]
    public async Task RealDaemon_StandardInput_ReachesTheChildAndChangesTheFileItWrites()
    {
        // The false-green defect, reproduced in the shape that produced it. Development Mode pipes its patch to git,
        // which reads it from standard input; with standard input unattached git reads end-of-file, applies nothing,
        // and exits zero — so a test asserting only the exit code passes against the broken build. What is asserted
        // here is the CONTENT the child wrote from what it read, which without standard input is an empty file while
        // the exit code is still zero.
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateAsync(options);
        const string Payload = "diff --git a/a.txt b/a.txt\n+++ piped through stdin\n";

        var result = await fixture.Provider.ExecuteAsync(fixture.Handle,
            new SandboxCommandRequest
            {
                ExecutionId = Guid.NewGuid().ToString("N"),
                Executable = "/bin/sh",
                Arguments = ["-c", "cat > applied.txt"],
                WorkingDirectory = "/",
                StandardInput = Payload
            });

        AssertEx.Equal(expected: 0, result.ExitCode);
        AssertEx.Equal(Payload, await File.ReadAllTextAsync(Path.Combine(fixture.WorkspaceRoot, "applied.txt")));
    }


    [Test]
    public async Task RealDaemon_ALargePayloadDoesNotDeadlockAgainstTheChildsOwnOutput()
    {
        // A Docker exec is one bidirectional connection. `tee` reads and writes at the same time, so a client that
        // sent the whole payload before draining output would fill the daemon's buffer, the daemon would stop
        // accepting the payload, and both sides would wait on each other until the timeout. Development Mode's patch
        // ceiling is 8 MB; two here is comfortably past where a serialised implementation stops working.
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateAsync(options);
        var payload = new string('p', count: 2 * 1024 * 1024);

        var result = await fixture.Provider.ExecuteAsync(fixture.Handle,
            new SandboxCommandRequest
            {
                ExecutionId = Guid.NewGuid().ToString("N"),
                Executable = "/bin/sh",
                Arguments = ["-c", "tee big.txt"],
                WorkingDirectory = "/",
                StandardInput = payload,
                Timeout = TimeSpan.FromSeconds(60)
            });

        AssertEx.True(result.Completed, "the command did not complete — a deadlock between the payload and the child's output.");
        AssertEx.Equal(payload.Length, (int)new FileInfo(Path.Combine(fixture.WorkspaceRoot, "big.txt")).Length);
    }


    [Test]
    public async Task RealDaemon_WhenNoStandardInputIsSupplied_TheChildStillSeesEndOfInputRatherThanHanging()
    {
        // The other half of the stdin change: attaching stdin unconditionally would leave every ordinary command
        // waiting on input nobody sends. A short timeout here would surface that as a cancelled, incomplete result.
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateAsync(options);

        var result = await fixture.Provider.ExecuteAsync(fixture.Handle,
            new SandboxCommandRequest
            {
                ExecutionId = Guid.NewGuid().ToString("N"),
                Executable = "/bin/sh",
                Arguments = ["-c", "echo done"],
                Timeout = TimeSpan.FromSeconds(20)
            });

        AssertEx.True(result.Completed);
        AssertEx.Equal("done", result.StandardOutput.Trim());
    }

    [Test]
    public async Task RealDaemon_KillAsync_RemovesTheContainerFromTheDaemon()
    {
        var options = await RequireDaemonAsync();
        var fixture = await ContainerFixture.CreateAsync(options);
        var containerId = fixture.ContainerId;

        await fixture.Provider.KillAsync(fixture.Handle);

        var probeClient = new DockerDotNetRuntimeClientFactory(new StaticOptionsMonitor<ContainerSandboxOptions>(options))
            .Create(DockerDaemonEndpointResolver.Resolve(options));
        await using (probeClient)
        {
            await AssertEx.ThrowsAsync<DockerRuntimeException>(() => probeClient.InspectContainerAsync(containerId));
        }

        await fixture.DisposeAsync();
    }

    /// <summary>
    ///     Skip-with-reason gate. Never returns a "daemon is fine" default and never lets a test pass without one — the
    ///     reason string is written to be read in CI output by someone wondering why the isolation tests are silent.
    /// </summary>
    private static async Task<ContainerSandboxOptions> RequireDaemonAsync()
    {
        var options = DockerSandboxHardeningTests.Options() with
        {
            Image = TestImage,
            ScratchSizeMb = 64,
            MemoryMb = 512,
            CpuCount = 2,
            PidsLimit = 256,
            DaemonProbeTimeoutSeconds = 10
        };

        var endpoint = DockerDaemonEndpointResolver.Resolve(options);
        await using var client = new DockerDotNetRuntimeClientFactory(new StaticOptionsMonitor<ContainerSandboxOptions>(options))
            .Create(endpoint);

        DockerDaemonIdentity identity;
        try
        {
            identity = await client.ProbeAsync();
        }
        catch (DockerRuntimeException exception)
        {
            throw new SkipTestException(
                $"SKIPPED — no usable Docker daemon: {exception.Status} at '{endpoint.Display}' ({exception.Message}). "
                + "These are the ONLY tests that prove the §3.8 hardening contract holds against a real daemon; a green run "
                + "without them is not evidence of isolation. Start Docker (or set DOCKER_HOST) and re-run.");
        }

        if (!identity.OperatingSystem.Equals("linux", StringComparison.OrdinalIgnoreCase))
        {
            throw new SkipTestException(
                $"SKIPPED — the reachable Docker daemon runs '{identity.OperatingSystem}' containers, not Linux. The §3.8 "
                + "contract's capability, namespace and read-only-rootfs guarantees are Linux semantics and cannot be verified here.");
        }

        return options;
    }

    private static async Task<string> ProbeAsync(ContainerFixture fixture, string shellCommand)
    {
        var result = await fixture.Provider.ExecuteAsync(fixture.Handle,
            new SandboxCommandRequest { ExecutionId = Guid.NewGuid().ToString("N"), Executable = "/bin/sh", Arguments = ["-c", shellCommand] });

        return (result.StandardOutput + result.StandardError).Trim();
    }

    /// <summary>A provider plus one verified container plus its host workspace, torn down together.</summary>
    private sealed class ContainerFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _workspace;

        private ContainerFixture(DockerSandboxRuntimeProvider provider,
            SandboxHandle handle,
            IDockerRuntimeClient client,
            string containerId,
            DockerContainerSpecification specification,
            TemporaryDirectory workspace)
        {
            Provider = provider;
            Handle = handle;
            Client = client;
            ContainerId = containerId;
            Specification = specification;
            _workspace = workspace;
        }

        public DockerSandboxRuntimeProvider Provider { get; }

        public SandboxHandle Handle { get; }

        public IDockerRuntimeClient Client { get; }

        public string ContainerId { get; }

        public DockerContainerSpecification Specification { get; }

        public string WorkspaceRoot => Path.Combine(_workspace.Path, "workspace");

        public static async Task<ContainerFixture> CreateAsync(ContainerSandboxOptions options)
        {
            var workspace = new TemporaryDirectory();
            var workspaceRoot = Path.Combine(workspace.Path, "workspace");
            Directory.CreateDirectory(workspaceRoot);

            var monitor = new StaticOptionsMonitor<ContainerSandboxOptions>(options);
            var factory = new DockerDotNetRuntimeClientFactory(monitor);
            var provider = new DockerSandboxRuntimeProvider(monitor, factory, new FixedTimeProvider(FixedNow),
                NullLogger<DockerSandboxRuntimeProvider>.Instance);

            var handle = await provider.CreateOrAttachAsync(new SandboxCreateRequest
            {
                AttachKey = new SandboxAttachKey
                {
                    OwnerUserId = Guid.NewGuid().ToString("N"),
                    NodeId = "integration-node",
                    ProviderName = DockerSandboxRuntimeProvider.Name,
                    RuntimeProfile = "development",
                    ManifestVersion = 1
                },
                RuntimeProfile = "development",
                NetworkPolicy = SandboxNetworkPolicy.None,
                TrustedHostWorkspace = new SandboxTrustedHostWorkspace { RootPath = workspaceRoot }
            });

            var specification = DockerSandboxHardening.BuildSpecification(options,
                new ResolvedContainerIdentity(options.UserId!.Value, options.GroupId!.Value),
                "xe-dev-" + handle.SandboxId,
                handle.SandboxId,
                [
                    new DockerBindMount
                    {
                        HostPath = Path.GetFullPath(workspaceRoot),
                        ContainerPath = options.WorkspaceMountTarget,
                        ReadOnly = false,
                        Propagation = "private"
                    }
                ]);

            var client = factory.Create(DockerDaemonEndpointResolver.Resolve(options));
            var containerId = await ResolveContainerIdAsync(client, "xe-dev-" + handle.SandboxId);

            return new ContainerFixture(provider, handle, client, containerId, specification, workspace);
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await Client.DisposeAsync();
            _workspace.Dispose();
        }

        /// <summary>
        ///     The provider's handle carries its own sandbox id, not the daemon's container id, so the container is
        ///     located by the deterministic name the provider gives it.
        /// </summary>
        private static async Task<string> ResolveContainerIdAsync(IDockerRuntimeClient client, string containerName)
        {
            var settings = await client.InspectContainerAsync(containerName);
            return settings.ContainerId;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-docker-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort teardown of a temp tree.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort teardown of a temp tree.
            }
        }
    }

    private sealed class FixedNodeDataDirectory : XE_Local_AI_Engine.Providers.Abstractions.INodeDataDirectory
    {
        public FixedNodeDataDirectory(string root)
        {
            Root = root;
        }

        public string Root { get; }
    }
}
