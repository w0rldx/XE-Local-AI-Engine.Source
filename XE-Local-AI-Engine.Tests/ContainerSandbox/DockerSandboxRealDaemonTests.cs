namespace XE_Local_AI_Engine.Tests.ContainerSandbox;

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.InteropServices;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Real-daemon integration coverage for the container hardening contract.
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

    /// <summary>
    ///     Set this to <c>1</c> where a daemon is promised (CI) and an absent one becomes a FAILURE instead of a skip.
    ///     "These tests did not run" is an environment fact on a laptop and a broken gate on a machine that has Docker,
    ///     and only the operator of that machine can tell the two apart — so they say which it is.
    /// </summary>
    private const string RequireDockerVariable = "XE_REQUIRE_DOCKER_TESTS";

    /// <summary>The credential the Development-shaped fixture commits, so a shadow has something real to hide.</summary>
    private const string ShadowedSecretContent = "-----BEGIN PRIVATE KEY-----\ndevmodesentinelvalue\n-----END PRIVATE KEY-----\n";

    private static readonly TimeSpan ImagePullTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     One probe and one image pull for the whole class. The 22 tests below run in parallel and every one of them
    ///     needs the same answer; probing (let alone pulling) per test would multiply the round trips by 22 for a
    ///     result that cannot differ. A skip decided here is rethrown to each awaiting test unchanged.
    /// </summary>
    private static readonly Lazy<Task<ContainerSandboxOptions>> DaemonGate = new(ResolveUsableDaemonAsync);

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

        AssertEx.Equal(fixture.Specification.User, settings.User);
        AssertEx.False(settings.Privileged);
        AssertEx.True(settings.ReadOnlyRootFilesystem);
        AssertEx.Contains(settings.CapabilitiesDropped, "ALL");
        AssertEx.Empty(settings.CapabilitiesAdded);
        AssertEx.Contains(settings.SecurityOptions, option => option.Contains("no-new-privileges", StringComparison.OrdinalIgnoreCase));

        // The observed seccomp rendering, recorded here because it is the thing the unit suite has to assume. Measured
        // against Engine 29.7.2 (rootless, API 1.55): a container created with a seccomp profile inspects back as
        // `seccomp=<compacted JSON>` — never as a path, and never as the profile's name — while a container created
        // WITHOUT one inspects back with SecurityOpt null, which is also what a daemon with seccomp disabled reports.
        // That indistinguishability is the whole reason the profile is passed explicitly.
        var seccomp = AssertEx.NotNull(settings.SecurityOptions.FirstOrDefault(option => option.StartsWith("seccomp=", StringComparison.Ordinal)),
            "the daemon applied no seccomp profile; it reported [" + string.Join(", ", settings.SecurityOptions.Select(option => option[..Math.Min(option.Length, 40)])) + "]");
        AssertEx.Contains(seccomp, "SCMP_ACT_ERRNO");
        AssertEx.False(seccomp.Contains("unconfined", StringComparison.OrdinalIgnoreCase), "the daemon applied an unconfined seccomp profile");
        AssertEx.Equal("none", settings.NetworkMode);
        AssertEx.Equal(expected: 0, settings.DeviceCount);
        AssertEx.NotEqual("host", settings.PidMode);
        AssertEx.NotEqual("host", settings.IpcMode);
        AssertEx.NotEqual("host", settings.UtsMode);
        AssertEx.Contains(settings.TemporaryFilesystems[options.ScratchMountTarget], "size=");
        AssertEx.Contains(settings.TemporaryFilesystems[options.TempMountTarget], "size=");
        AssertEx.Equal((long)options.MemoryMb * 1024 * 1024, settings.MemoryBytes);
        AssertEx.Equal((long)(options.CpuCount * 1_000_000_000d), settings.NanoCpus);
        AssertEx.Equal((long)options.PidsLimit, settings.PidsLimit);
        AssertEx.ContainsSingle(settings.Mounts,
            mount => string.Equals(mount.ContainerPath, options.WorkspaceMountTarget, StringComparison.Ordinal)
                     && string.Equals(mount.Propagation, "private", StringComparison.Ordinal));

        // And the verifier agrees, which is the assertion the provider actually makes.
        AssertEx.Empty(DockerSandboxHardening.FindViolations(fixture.Specification, settings, fixture.DaemonIsRootless));
    }

    [Test]
    public async Task RealDaemon_InsideTheContainer_TheGuaranteesActuallyHold()
    {
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateAsync(options);

        // The inspect read-back says what the daemon recorded. These say what the kernel did — a stronger claim, and
        // the one the operator's threat model rests on.
        var expectedIdentity = fixture.Specification.User.Split(':', 2);
        AssertEx.Equal(expectedIdentity[0], await ProbeAsync(fixture, "id -u"));
        AssertEx.Equal(expectedIdentity[1], await ProbeAsync(fixture, "id -g"));
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
    public async Task RealDaemon_TheToolchainTemporaryDirectoryIsWritableAndStillRefusesToExecute()
    {
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateAsync(options);

        // Two claims, and the mount is only defensible if BOTH hold. Writable, because the .NET runtime puts the
        // shared-memory files backing a named Mutex under a path it compiles in and no environment variable moves —
        // without this, the first `dotnet` invocation fails EROFS and the whole validation gate is unreachable.
        AssertEx.Equal("TMP-WRITABLE",
            await ProbeAsync(fixture, $"touch {options.TempMountTarget}/probe && echo TMP-WRITABLE"));

        // And still noexec, from inside, where the kernel answers rather than the daemon. A copy of a known-good
        // binary with the execute bit set is refused, which is what the mount option becoming real looks like.
        AssertEx.Equal("EXEC-REFUSED",
            await ProbeAsync(fixture,
                $"cp /bin/sh {options.TempMountTarget}/payload && chmod +x {options.TempMountTarget}/payload && "
                + $"{options.TempMountTarget}/payload -c 'echo EXEC-ALLOWED' 2>/dev/null || echo EXEC-REFUSED"));
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
            new SandboxCopyRequest
            {
                SourcePath = "/export.txt",
                DestinationPath = destination
            });

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
    public async Task RealDaemon_CopyInto_LandsInTheWorkspaceAndTheContainerCanReadIt()
    {
        // write_file is built on copy-into, which threw unconditionally. Docker refuses archive extraction into a
        // read-only-rootfs container, so this goes through the host side of the bind mount — and the assertion that
        // matters is that the CONTAINER can read what the engine wrote.
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateAsync(options);
        var source = Path.Combine(fixture.WorkspaceRoot, "..", "copy-in-source.txt");
        await File.WriteAllTextAsync(source, "written-by-the-engine");

        await fixture.Provider.CopyIntoAsync(fixture.Handle,
            new SandboxCopyRequest
            {
                SourcePath = source,
                DestinationPath = "/nested/written.txt"
            });

        AssertEx.Equal("written-by-the-engine",
            await File.ReadAllTextAsync(Path.Combine(fixture.WorkspaceRoot, "nested", "written.txt")));
        AssertEx.Equal("written-by-the-engine", await ProbeAsync(fixture, $"cat {options.WorkspaceMountTarget}/nested/written.txt"));
    }

    [Test]
    public async Task RealDaemon_CopyInto_RefusesADestinationThatEscapesTheWorkspace()
    {
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateAsync(options);
        var source = Path.Combine(fixture.WorkspaceRoot, "..", "escape-source.txt");
        await File.WriteAllTextAsync(source, "should-not-land");

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Provider.CopyIntoAsync(fixture.Handle,
            new SandboxCopyRequest
            {
                SourcePath = source,
                DestinationPath = "/../escaped.txt"
            }));

        AssertEx.False(File.Exists(Path.Combine(fixture.WorkspaceRoot, "..", "escaped.txt")));
    }

    [Test]
    public async Task RealDaemon_CopyInto_RefusesToWriteThroughASymlinkThePreviousCommandPlanted()
    {
        // Not hypothetical: the container can create the symlink, and it is the HOST that resolves it when the engine
        // writes. Planted from inside the container, exactly as a compromised build step would.
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateAsync(options);
        var outside = Path.Combine(fixture.WorkspaceRoot, "..", "outside");
        Directory.CreateDirectory(outside);

        AssertEx.Equal("PLANTED",
            await ProbeAsync(fixture, $"ln -s /tmp {options.WorkspaceMountTarget}/escape && echo PLANTED"));

        var source = Path.Combine(fixture.WorkspaceRoot, "..", "symlink-source.txt");
        await File.WriteAllTextAsync(source, "should-not-land");

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Provider.CopyIntoAsync(fixture.Handle,
            new SandboxCopyRequest
            {
                SourcePath = source,
                DestinationPath = "/escape/planted.txt"
            }));
    }

    [Test]
    public async Task RealDaemon_WhenNoneIsRequested_EgressIsDenied()
    {
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateAsync(options, SandboxNetworkPolicy.None);

        var settings = await fixture.Client.InspectContainerAsync(fixture.ContainerId);
        AssertEx.Equal("none", settings.NetworkMode);
        AssertEx.Equal("NET-BLOCKED",
            await ProbeAsync(fixture, "wget -T2 -q -O- http://1.1.1.1 >/dev/null 2>&1 && echo NET-OK || echo NET-BLOCKED"));
    }

    /// <summary>
    ///     G5 on the container backend. The unit suite can only assert what the engine ASKED for; this is what the
    ///     daemon did with it — the container reads an EMPTY <c>certs/server.pem</c> while the same file on the host is
    ///     still the credential, byte for byte. The second half is the load-bearing one: a scheme that neutralized the
    ///     secret by mutating the worktree would make the tree dirty against its base commit, and an apply would carry
    ///     the deletion back to the operator's real repository.
    /// </summary>
    [Test]
    public async Task RealDaemon_AShadowedCredentialReadsBackEmptyInsideWhileTheHostFileIsUntouched()
    {
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateWithRuntimeMountsAsync(options);

        AssertEx.Equal("EMPTY", await ProbeAsync(fixture, "test -s /workspace/certs/server.pem && echo NOT-EMPTY || echo EMPTY"));
        // Contains, not Equal: ProbeAsync joins stdout and stderr, and the shell's own "Read-only file system"
        // diagnostic is exactly the evidence being asserted.
        AssertEx.Contains(await ProbeAsync(fixture, "echo x >> /workspace/certs/server.pem && echo WRITABLE || echo READONLY"), "READONLY");

        var onHost = await File.ReadAllTextAsync(Path.Combine(fixture.WorkspaceRoot, "certs", "server.pem"));
        AssertEx.Equal(ShadowedSecretContent, onHost);
    }

    /// <summary>
    ///     G1(c) on the container backend, composed the way the caller composes it. The flag and the mechanism are two
    ///     separate facts; this asserts they MEET on a Development-shaped container — the workspace plus the four
    ///     per-task runtime mounts plus the read-only <c>.git/config</c> — and that the daemon really applied it.
    /// </summary>
    [Test]
    public async Task RealDaemon_DevelopmentShapedRun_ReadsBackNoEgressFromTheCapabilityGatedDecision()
    {
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateWithRuntimeMountsAsync(options);

        // Exactly the caller-side decision DevelopmentWorkspaceProvider.ResolveAgentFacingNetworkPolicy makes.
        var resolved = fixture.Provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy)
            ? SandboxNetworkPolicy.None
            : SandboxNetworkPolicy.Unrestricted;
        AssertEx.Equal(SandboxNetworkPolicy.None,
            resolved,
            "the container backend advertises network policy, so Development Mode must ask it for denial rather than fall back.");

        var settings = await fixture.Client.InspectContainerAsync(fixture.ContainerId);
        AssertEx.Equal("none", settings.NetworkMode);
        AssertEx.Equal("NET-BLOCKED",
            await ProbeAsync(fixture, "wget -T2 -q -O- http://1.1.1.1 >/dev/null 2>&1 && echo NET-OK || echo NET-BLOCKED"));

        // Everything else the hardening contract requires still holds on the denied container.
        AssertEx.Empty(DockerSandboxHardening.FindViolations(fixture.Specification, settings, fixture.DaemonIsRootless));
    }

    [Test]
    public async Task RealDaemon_WhenUnrestrictedIsRequested_TheContainerIsCreatedOnTheDefaultBridge()
    {
        // Development Mode still requests this — for its WARM RESTORE sandbox, the short-lived one that fills the
        // package cache from the base commit before the agent-facing sandbox is created. After G1 the agent-facing
        // sandbox asks for `None` (see the test above); this posture did not become dead, it moved. Egress itself is
        // not asserted here — that would make the suite depend on this machine having working outbound DNS — but the
        // applied network mode is read back off the daemon, which is the guarantee the provider makes.
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateAsync(options, SandboxNetworkPolicy.Unrestricted);

        var settings = await fixture.Client.InspectContainerAsync(fixture.ContainerId);

        AssertEx.Equal("bridge", settings.NetworkMode);
        AssertEx.NotEqual("host", settings.NetworkMode);
        // Everything else the contract requires still holds; only egress moved.
        AssertEx.Empty(DockerSandboxHardening.FindViolations(fixture.Specification, settings, fixture.DaemonIsRootless));
    }

    [Test]
    public async Task RealDaemon_WhatTheContainerWritesIntoTheWorkspaceIsOwnedByThisEngine()
    {
        // The hardening invariant got backwards, verified the only way it can be. `inspect` echoes back the uid that was
        // ASKED for and can never say what that uid maps to, so this writes from inside the container and stats the
        // result host-side. Measured on this box (Engine 29.6.1 rootless, /etc/subuid w0rldx:100000:65536): container
        // uid 0 lands host-side as uid 1000 (the engine), while container uid 1000 would be host uid 100999 and could
        // not create the file at all.
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateAsync(options);

        AssertEx.Equal("WROTE", await ProbeAsync(fixture, $"touch {options.WorkspaceMountTarget}/owned.txt && echo WROTE"));

        var hostPath = Path.Combine(fixture.WorkspaceRoot, "owned.txt");
        AssertEx.True(File.Exists(hostPath), "the file the container created is not visible on the host side of the mount.");
        var owner = DockerWorkspaceHostFiles.TryReadOwnerUserId(hostPath);
        AssertEx.True(owner.HasValue, "the owner of the container-created file could not be read on the host.");
        AssertEx.Equal(LibC.GetEffectiveUserId(), owner!.Value);

        // And the round trip closes: the engine can rewrite what the container created.
        await File.WriteAllTextAsync(hostPath, "engine-rewrote-it");
        AssertEx.Equal("engine-rewrote-it", await ProbeAsync(fixture, $"cat {options.WorkspaceMountTarget}/owned.txt"));
    }

    [Test]
    // CA2000 is exactly right about the `crashed` provider below, and leaving it undisposed is exactly the point: its
    // DisposeAsync is the graceful teardown whose ABSENCE creates the leak this test sweeps. The container it created
    // is removed either by the sweep under test or by the finally block, so nothing is left on the daemon.
    [SuppressMessage("Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The undisposed provider models the crash this test exists to recover from; its container is removed in the finally block.")]
    public async Task RealDaemon_StartupSweep_CollectsAContainerAPreviousRunLeaked()
    {
        // The restart, modelled literally: a second provider over the SAME node data directory is what the next
        // process start is, and its registry is empty for the same reason — the first one's died with it. Against a
        // real daemon rather than the fake because the property under test is the daemon-side label filter, which the
        // fake can only imitate.
        var options = await RequireDaemonAsync();
        using var workspace = new TemporaryDirectory();
        var workspaceRoot = Path.Combine(workspace.Path, "workspace");
        Directory.CreateDirectory(workspaceRoot);

        var monitor = new StaticOptionsMonitor<ContainerSandboxOptions>(options);
        var request = new SandboxCreateRequest
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
            TrustedHostWorkspace = new SandboxTrustedHostWorkspace
            {
                RootPath = workspaceRoot
            }
        };

        // Deliberately NOT disposed: disposing is the graceful teardown whose absence creates the leak.
        var crashed = new DockerSandboxRuntimeProvider(monitor,
            new DockerDotNetRuntimeClientFactory(monitor),
            new FixedNodeDataDirectory(workspace.Path),
            new FixedTimeProvider(FixedNow),
            NullLogger<DockerSandboxRuntimeProvider>.Instance);

        var handle = await crashed.CreateOrAttachAsync(request);
        await using var client = new DockerDotNetRuntimeClientFactory(monitor).Create(DockerDaemonEndpointResolver.Resolve(options));
        // The handle carries the provider's sandbox id, not the daemon's container id, so the container is located by
        // the deterministic name the provider gives it.
        var leaked = (await client.InspectContainerAsync("xe-dev-" + handle.SandboxId)).ContainerId;

        await using var restarted = new DockerSandboxRuntimeProvider(monitor,
            new DockerDotNetRuntimeClientFactory(monitor),
            new FixedNodeDataDirectory(workspace.Path),
            new FixedTimeProvider(FixedNow),
            NullLogger<DockerSandboxRuntimeProvider>.Instance);

        try
        {
            AssertEx.Equal(expected: 1, await restarted.SweepOrphanedContainersAsync());
            AssertEx.Empty(await client.ListContainersAsync(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DockerSandboxHardening.OwnerLabel] = DockerSandboxHardening.OwnerLabelValue,
                [DockerSandboxHardening.InstallLabel] = DockerSandboxRuntimeProvider.BuildInstallId(workspace.Path)
            }));

            // Idempotent against a real daemon too: the second start of a swept node finds nothing.
            AssertEx.Equal(expected: 0, await restarted.SweepOrphanedContainersAsync());
        }
        finally
        {
            // Belt and braces: if the sweep did not remove it, this test must not leave a container on the box.
            await client.RemoveContainerAsync(leaked);
        }
    }

    [Test]
    public async Task RealDaemon_StartupSweep_LeavesAnotherInstallationsContainerAlone()
    {
        var options = await RequireDaemonAsync();
        using var theirWorkspace = new TemporaryDirectory();
        using var ourWorkspace = new TemporaryDirectory();
        var workspaceRoot = Path.Combine(theirWorkspace.Path, "workspace");
        Directory.CreateDirectory(workspaceRoot);

        var monitor = new StaticOptionsMonitor<ContainerSandboxOptions>(options);
        await using var theirs = new DockerSandboxRuntimeProvider(monitor,
            new DockerDotNetRuntimeClientFactory(monitor),
            new FixedNodeDataDirectory(theirWorkspace.Path),
            new FixedTimeProvider(FixedNow),
            NullLogger<DockerSandboxRuntimeProvider>.Instance);

        var handle = await theirs.CreateOrAttachAsync(new SandboxCreateRequest
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
            TrustedHostWorkspace = new SandboxTrustedHostWorkspace
            {
                RootPath = workspaceRoot
            }
        });

        await using var ours = new DockerSandboxRuntimeProvider(monitor,
            new DockerDotNetRuntimeClientFactory(monitor),
            new FixedNodeDataDirectory(ourWorkspace.Path),
            new FixedTimeProvider(FixedNow),
            NullLogger<DockerSandboxRuntimeProvider>.Instance);

        AssertEx.Equal(expected: 0, await ours.SweepOrphanedContainersAsync());

        // Still there, and still usable by the installation that owns it — which is the whole point of the install
        // label. A sweep keyed on the owner label alone would have taken this one out from under a running engine.
        await using var client = new DockerDotNetRuntimeClientFactory(monitor).Create(DockerDaemonEndpointResolver.Resolve(options));
        var settings = await client.InspectContainerAsync("xe-dev-" + handle.SandboxId);
        AssertEx.Equal("none", settings.NetworkMode);
    }

    [Test]
    public async Task RealDaemon_AnIdentityThatDoesNotMapToThisEngine_IsRefusedAndTheContainerRemoved()
    {
        // The measurement that inverted the rule, as a regression pin. Under this box's rootless daemon the hardening
        // mandate of `--user 1000:1000` produces a container that cannot write a byte into its own workspace — and
        // every hardening read-back still passes, because the daemon applied exactly what it was told. Only the probe
        // catches it. Skipped rather than inverted on a rootful daemon, where 1000 is the correct answer.
        var options = await RequireDaemonAsync();
        if (!await IsRootlessAsync(options))
        {
            throw new SkipTestException("SKIPPED — this daemon is not rootless, so an in-container uid maps straight through and there is no "
                                        + "mis-mapping to reproduce. The inverted-identity case is only reachable against a rootless daemon.");
        }

        var mismatched = options with
        {
            UserId = 1000,
            GroupId = 1000
        };
        using var workspace = new TemporaryDirectory();
        var workspaceRoot = Path.Combine(workspace.Path, "workspace");
        Directory.CreateDirectory(workspaceRoot);

        var monitor = new StaticOptionsMonitor<ContainerSandboxOptions>(mismatched);
        await using var provider = new DockerSandboxRuntimeProvider(monitor,
            new DockerDotNetRuntimeClientFactory(monitor),
            new FixedNodeDataDirectory(workspace.Path),
            new FixedTimeProvider(FixedNow),
            NullLogger<DockerSandboxRuntimeProvider>.Instance);

        var request = new SandboxCreateRequest
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
            TrustedHostWorkspace = new SandboxTrustedHostWorkspace
            {
                RootPath = workspaceRoot
            }
        };

        // Manual throw-capture rather than AssertEx.ThrowsAsync: this creates a container directly, so an absent
        // pinned image surfaces here too and is reported the same way ContainerFixture.CreateAsync reports it.
        SandboxCapabilityNotSupportedException? refusal = null;
        try
        {
            await provider.CreateOrAttachAsync(request);
        }
        catch (SandboxCapabilityNotSupportedException caught)
        {
            refusal = caught;
        }
        catch (DockerRuntimeException dockerException) when (dockerException.InnerException is DockerApiException { StatusCode: HttpStatusCode.NotFound })
        {
            throw Unavailable($"the pinned test image '{options.Image}' is not present on this daemon, although the gate ensured it before this "
                              + $"test ran — it was removed mid-run, or the daemon changed. Run `docker pull {options.Image}` and re-run.");
        }

        var exception = AssertEx.NotNull(refusal);
        AssertEx.Contains(exception.Message, "workspace mount");
        AssertEx.Empty(Directory.GetFileSystemEntries(workspaceRoot));
    }

    [Test]
    public async Task RealDaemon_TheEngineGeneratedRuntimeMountsAreVisibleAndWritableFromInside()
    {
        // The measured failure this closes: BuildEnvironment pointed HOME, TMPDIR, NUGET_PACKAGES and DOTNET_CLI_HOME
        // at absolute HOST paths. Inside a container those do not exist and the rootfs is read-only, so `dotnet
        // restore` / `build` / `test` all fail. Asserting a WRITE rather than existence is the point — a mount that is
        // present but not writable fails restore just as completely.
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateWithRuntimeMountsAsync(options);

        foreach (var name in new[]
                 {
                     "home",
                     "tmp",
                     "nuget",
                     "dotnet"
                 })
        {
            AssertEx.Equal("WROTE", await ProbeAsync(fixture, $"touch /xe-runtime/{name}/probe && echo WROTE"));
            AssertEx.True(File.Exists(Path.Combine(fixture.RuntimeRoot, name, "probe")),
                $"the container's write to /xe-runtime/{name} is not visible on the host side of the mount.");
        }

        // And the handle answers the translation question the engine actually asks.
        AssertEx.Equal("/xe-runtime/nuget", fixture.Handle.TryResolveSandboxPath(Path.Combine(fixture.RuntimeRoot, "nuget")));
    }

    [Test]
    public async Task RealDaemon_TheWorkspaceControlManifestIsNotReachableFromInsideTheContainer()
    {
        // D9. workspace.json sits directly in the runtime root and holds the repository identity, the selected folder
        // and the base commit. Mounting the four named subdirectories rather than their parent is the whole of the
        // exclusion, so this asserts the parent is not reachable by any route the mounts provide.
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateWithRuntimeMountsAsync(options);
        await File.WriteAllTextAsync(Path.Combine(fixture.RuntimeRoot, "workspace.json"), "{\"baseCommit\":\"secret\"}");

        AssertEx.Equal("ABSENT", await ProbeAsync(fixture, "cat /xe-runtime/workspace.json 2>/dev/null && echo READABLE || echo ABSENT"));
        AssertEx.Equal("ABSENT", await ProbeAsync(fixture, "cat /xe-runtime/../workspace.json 2>/dev/null && echo READABLE || echo ABSENT"));
        AssertEx.False((await ProbeAsync(fixture, "cat /xe-runtime/workspace.json 2>&1")).Contains("secret", StringComparison.Ordinal));

        // The fixture really did write it, so the assertions above are about the mount and not about a missing file.
        AssertEx.True(File.Exists(Path.Combine(fixture.RuntimeRoot, "workspace.json")));
    }

    [Test]
    public async Task RealDaemon_TheGitConfigMountIsReadOnlyAndUnremovableWhileTheWorkTreeStaysWritable()
    {
        // The container-side closure of the .git/config execution vector, and all four halves matter: the config is
        // unwritable AND unremovable (it is a mount point, so `rm` answers "Resource busy"), while the work tree stays
        // writable so the agent can edit and .git/index stays writable so `git apply --index` still works.
        var options = await RequireDaemonAsync();
        await using var fixture = await ContainerFixture.CreateWithRuntimeMountsAsync(options);

        // Matched rather than compared: a failed shell REDIRECTION reports on the shell's own stderr, which `2>/dev/null`
        // on the command does not cover, so the probe legitimately carries the kernel's message alongside the verdict.
        // Asserting the negative too is what stops "contains READONLY" from passing on output that also said WRITABLE.
        var write = await ProbeAsync(fixture, $"echo pwn >> {options.WorkspaceMountTarget}/.git/config 2>/dev/null && echo WRITABLE || echo READONLY");
        AssertEx.Contains(write, "READONLY");
        AssertEx.False(write.Contains("WRITABLE", StringComparison.Ordinal), write);

        var remove = await ProbeAsync(fixture, $"rm -f {options.WorkspaceMountTarget}/.git/config 2>/dev/null && echo REMOVED || echo BUSY");
        AssertEx.Contains(remove, "BUSY");
        AssertEx.False(remove.Contains("REMOVED", StringComparison.Ordinal), remove);
        AssertEx.Equal("TREE-WRITABLE",
            await ProbeAsync(fixture, $"touch {options.WorkspaceMountTarget}/edited.cs && echo TREE-WRITABLE"));
        AssertEx.Equal("INDEX-WRITABLE",
            await ProbeAsync(fixture, $"touch {options.WorkspaceMountTarget}/.git/index && echo INDEX-WRITABLE"));

        // The host side is untouched by any of it.
        AssertEx.Equal("[core]\n", await File.ReadAllTextAsync(Path.Combine(fixture.WorkspaceRoot, ".git", "config")));
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
    ///     With <c>XE_REQUIRE_DOCKER_TESTS=1</c> the same conditions fail the test instead of skipping it.
    /// </summary>
    private static Task<ContainerSandboxOptions> RequireDaemonAsync()
    {
        return DaemonGate.Value;
    }

    /// <summary>
    ///     Find a daemon these tests can actually use and make the pinned image present on it, or decide — once — that
    ///     there is none.
    /// </summary>
    private static async Task<ContainerSandboxOptions> ResolveUsableDaemonAsync()
    {
        var options = DockerSandboxHardeningTests.Options() with
        {
            Image = TestImage,
            // Left unset ON PURPOSE. Which in-container id can use an engine-generated bind mount is a property of
            // the daemon, not of this test: a rootful daemon wants the engine's own uid, a rootless one wants 0.
            // Pinning 1000 here would pass on one machine and fail on the other for reasons unrelated to the contract.
            UserId = null,
            GroupId = null,
            ScratchSizeMb = 64,
            MemoryMb = 512,
            CpuCount = 2,
            PidsLimit = 256,
            DaemonProbeTimeoutSeconds = 10
        };

        var attempts = new List<string>();
        foreach (var candidate in DaemonCandidates(options))
        {
            var endpoint = DockerDaemonEndpointResolver.Resolve(candidate);
            DockerDaemonIdentity identity;

            await using (var client = new DockerDotNetRuntimeClientFactory(new StaticOptionsMonitor<ContainerSandboxOptions>(candidate))
                             .Create(endpoint))
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
                                  + "The §3.8 contract's capability, namespace and read-only-rootfs guarantees are Linux semantics and cannot be "
                                  + "verified here.");
            }

            await EnsurePinnedImageAsync(endpoint, TestImage);
            return candidate;
        }

        throw Unavailable("no usable Docker daemon. Tried " + string.Join(" | ", attempts)
                                                            + " Start Docker, or point DOCKER_HOST at a daemon this user can open, and re-run.");
    }

    /// <summary>
    ///     The endpoints to try, each named by the PRODUCTION resolver so these tests exercise the discovery the
    ///     product ships rather than a test-only shortcut.
    ///     <para>
    ///         The second candidate exists because the resolver deliberately stops at an <em>existing</em>
    ///         <c>/var/run/docker.sock</c> and never falls through to the per-user socket: a node must not silently
    ///         migrate between daemons, because which daemon is in use is precisely what the operator attests to. On a
    ///         box that has a root-owned system socket this user cannot open <em>and</em> a rootless daemon of their
    ///         own, that correct product behaviour left every test in this class skipped. Production is right to stop
    ///         and ask the operator; a test may keep looking — through the same resolver, by naming the socket in
    ///         <c>DaemonEndpoint</c>, its highest-priority input. The fixtures then reach the same daemon because they
    ///         re-resolve from the options returned here.
    ///     </para>
    /// </summary>
    private static IEnumerable<ContainerSandboxOptions> DaemonCandidates(ContainerSandboxOptions options)
    {
        yield return options;

        if (OperatingSystem.IsWindows())
        {
            yield break;
        }

        // Only when the resolver fell back to the platform default. If DOCKER_HOST or configuration NAMED a daemon,
        // that daemon is the subject of the run: quietly testing a different one would turn a misconfiguration into a
        // green suite about the wrong engine.
        if (DockerDaemonEndpointResolver.Resolve(options).Source != DockerDaemonEndpointSource.DefaultUnixSocket)
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
    ///     Make the pinned image present, pulling it once if it is not. The PRODUCT still never auto-pulls — a
    ///     Development Mode node assumes a provisioned image and <c>DockerSandboxRuntimeProvider</c> is unchanged — but
    ///     a suite that skipped itself because a fresh CI runner had no <c>alpine</c> was the other half of this gate
    ///     being hollow, and a hollow gate is the one thing this class exists not to be.
    /// </summary>
    private static async Task EnsurePinnedImageAsync(DockerDaemonEndpoint endpoint, string image)
    {
        using var client = new DockerClientBuilder().WithEndpoint(endpoint.Uri).WithTimeout(ImagePullTimeout).Build();

        try
        {
            await client.Images.InspectImageAsync(image);
            return;
        }
        catch (DockerApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // Absent, so pull it below. Any other failure is a real fault and belongs to the caller's probe report.
        }

        // Split at the digest: the engine's own clients send `fromImage=<name>&tag=sha256:<digest>`, and the pin is the
        // whole point — a bare tag would let a different image answer to the same name.
        var reference = image.Split('@', count: 2);
        var parameters = new ImagesCreateParameters
        {
            FromImage = reference[0],
            Tag = reference.Length == 2 ? reference[1] : null
        };

        using var timeout = new CancellationTokenSource(ImagePullTimeout);
        try
        {
            await client.Images.CreateImageAsync(parameters, authConfig: null, new Progress<JSONMessage>(), timeout.Token);
        }
        catch (Exception exception) when (exception is DockerApiException or HttpRequestException or OperationCanceledException)
        {
            throw Unavailable($"the pinned test image '{image}' is not on the daemon at '{endpoint.Display}' and pulling it failed "
                              + $"({exception.Message}). Run `docker pull {image}` and re-run.");
        }
    }

    /// <summary>
    ///     How a missing prerequisite is reported: a skip that names what was missing, or — when
    ///     <c>XE_REQUIRE_DOCKER_TESTS=1</c> — a failure. Both carry the same reason, so the CI output reads the same
    ///     either way; only the verdict changes.
    /// </summary>
    private static Exception Unavailable(string reason)
    {
        var message = reason + " These are the ONLY tests that prove the §3.8 hardening contract holds against a real daemon; "
                             + "a green run without them is not evidence of isolation.";

        return string.Equals(Environment.GetEnvironmentVariable(RequireDockerVariable), "1", StringComparison.Ordinal)
            ? new InvalidOperationException($"REQUIRED — {RequireDockerVariable}=1, so this is a failure rather than a skip: {message}")
            : new SkipTestException($"SKIPPED — {message}");
    }

    /// <summary>Whether the reachable daemon reports itself rootless, read through the same probe production uses.</summary>
    private static async Task<bool> IsRootlessAsync(ContainerSandboxOptions options)
    {
        await using var client = new DockerDotNetRuntimeClientFactory(new StaticOptionsMonitor<ContainerSandboxOptions>(options))
            .Create(DockerDaemonEndpointResolver.Resolve(options));

        return (await client.ProbeAsync()).IsRootless;
    }

    /// <summary>The engine process's own effective ids. The tests need them for the same reason the provider does.</summary>
    private static class LibC
    {
        [DllImport("libc", EntryPoint = "geteuid")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        public static extern uint GetEffectiveUserId();

        [DllImport("libc", EntryPoint = "getegid")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        public static extern uint GetEffectiveGroupId();
    }

    private static async Task<string> ProbeAsync(ContainerFixture fixture, string shellCommand)
    {
        var result = await fixture.Provider.ExecuteAsync(fixture.Handle,
            new SandboxCommandRequest
            {
                ExecutionId = Guid.NewGuid().ToString("N"),
                Executable = "/bin/sh",
                Arguments = ["-c", shellCommand]
            });

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
            bool daemonIsRootless,
            TemporaryDirectory workspace)
        {
            Provider = provider;
            Handle = handle;
            Client = client;
            ContainerId = containerId;
            Specification = specification;
            DaemonIsRootless = daemonIsRootless;
            _workspace = workspace;
        }

        public DockerSandboxRuntimeProvider Provider { get; }

        public SandboxHandle Handle { get; }

        public IDockerRuntimeClient Client { get; }

        public string ContainerId { get; }

        public DockerContainerSpecification Specification { get; }

        public bool DaemonIsRootless { get; }

        public string WorkspaceRoot => Path.Combine(_workspace.Path, "workspace");

        public string RuntimeRoot => Path.Combine(_workspace.Path, "runtime");

        /// <summary>
        ///     The fixture in the shape Development Mode actually asks for: the workspace, the four per-task runtime
        ///     subdirectories, and a read-only <c>.git/config</c> nested inside the workspace. Deliberately mounts the
        ///     four subdirectories and NOT their parent, which is what keeps <c>workspace.json</c> out (D9).
        /// </summary>
        public static Task<ContainerFixture> CreateWithRuntimeMountsAsync(ContainerSandboxOptions options)
        {
            return CreateAsync(options, SandboxNetworkPolicy.None, withRuntimeMounts: true);
        }

        public static async Task<ContainerFixture> CreateAsync(ContainerSandboxOptions options,
            SandboxNetworkPolicy networkPolicy = SandboxNetworkPolicy.None,
            bool withRuntimeMounts = false)
        {
            var workspace = new TemporaryDirectory();
            var workspaceRoot = Path.Combine(workspace.Path, "workspace");
            Directory.CreateDirectory(workspaceRoot);

            var runtimeRoot = Path.Combine(workspace.Path, "runtime");
            var mounts = new List<SandboxMount>();
            if (withRuntimeMounts)
            {
                foreach (var name in new[]
                         {
                             "home",
                             "tmp",
                             "nuget",
                             "dotnet"
                         })
                {
                    Directory.CreateDirectory(Path.Combine(runtimeRoot, name));
                    mounts.Add(new SandboxMount
                    {
                        HostPath = Path.Combine(runtimeRoot, name),
                        SandboxPath = "/xe-runtime/" + name,
                        ReadOnly = false
                    });
                }

                Directory.CreateDirectory(Path.Combine(workspaceRoot, ".git"));
                await File.WriteAllTextAsync(Path.Combine(workspaceRoot, ".git", "config"), "[core]\n");
                await File.WriteAllTextAsync(Path.Combine(workspaceRoot, ".git", "index"), string.Empty);
                mounts.Add(new SandboxMount
                {
                    HostPath = Path.Combine(workspaceRoot, ".git", "config"),
                    SandboxPath = "/.git/config",
                    ReadOnly = true
                });

                // G5's shadow, in the exact shape DevelopmentWorkspaceProvider requests it: a committed credential in
                // the workspace, an engine-generated EMPTY file OUTSIDE it, and a workspace-relative target so the
                // empty one lands on top of the real one. The real file is left byte-unchanged on disk, which is what
                // keeps the diff model from seeing a deletion.
                Directory.CreateDirectory(Path.Combine(workspaceRoot, "certs"));
                await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "certs", "server.pem"), ShadowedSecretContent);
                var shadowRoot = Path.Combine(runtimeRoot, "shadow");
                Directory.CreateDirectory(shadowRoot);
                var shadowSource = Path.Combine(shadowRoot, "server-pem");
                await File.WriteAllBytesAsync(shadowSource, []);
                mounts.Add(new SandboxMount
                {
                    HostPath = shadowSource,
                    SandboxPath = "/certs/server.pem",
                    TargetIsWorkspaceRelative = true,
                    ReadOnly = true
                });
            }

            var monitor = new StaticOptionsMonitor<ContainerSandboxOptions>(options);
            var factory = new DockerDotNetRuntimeClientFactory(monitor);
            var provider = new DockerSandboxRuntimeProvider(monitor,
                factory,
                new FixedNodeDataDirectory(workspace.Path),
                new FixedTimeProvider(FixedNow),
                NullLogger<DockerSandboxRuntimeProvider>.Instance);

            var request = new SandboxCreateRequest
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
                NetworkPolicy = networkPolicy,
                TrustedHostWorkspace = new SandboxTrustedHostWorkspace
                {
                    RootPath = workspaceRoot
                },
                Mounts = mounts
            };

            SandboxHandle handle;
            try
            {
                handle = await provider.CreateOrAttachAsync(request);
            }
            catch (DockerRuntimeException exception) when (exception.InnerException is DockerApiException { StatusCode: HttpStatusCode.NotFound })
            {
                // Daemon reachable but the pinned image is not present. The gate pulls it before any test runs, so
                // reaching this is a mid-run change of image or of daemon rather than an unprovisioned machine; it is
                // reported through the same Unavailable() path as every other missing prerequisite.
                workspace.Dispose();
                throw Unavailable($"the pinned test image '{options.Image}' is not present on this daemon, although the gate ensured it before this "
                                  + $"test ran — it was removed mid-run, or the daemon changed. Run `docker pull {options.Image}` and re-run.");
            }

            var client = factory.Create(DockerDaemonEndpointResolver.Resolve(options));
            var daemon = await client.ProbeAsync();

            // Rebuilt from what the handle REPORTS rather than from the request, so the specification these tests
            // verify against is the one the provider actually applied — including the derived container target for a
            // mount nested inside the workspace.
            var specification = DockerSandboxHardening.BuildSpecification(options,
                DockerSandboxRuntimeProvider.ResolveIdentity(options,
                    daemon.IsRootless,
                    () => (int)LibC.GetEffectiveUserId(),
                    () => (int)LibC.GetEffectiveGroupId()),
                "xe-dev-" + handle.SandboxId,
                handle.SandboxId,
                DockerSandboxRuntimeProvider.BuildInstallId(workspace.Path),
                [
                    .. handle.Mounts.Select(mount => new DockerBindMount
                    {
                        HostPath = mount.HostPath,
                        ContainerPath = mount.SandboxPath,
                        ReadOnly = mount.ReadOnly,
                        Propagation = "private"
                    })
                ],
                requestedLimits: null,
                networkPolicy);

            var containerId = await ResolveContainerIdAsync(client, "xe-dev-" + handle.SandboxId);

            return new ContainerFixture(provider, handle, client, containerId, specification, daemon.IsRootless, workspace);
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

    private sealed class FixedNodeDataDirectory : INodeDataDirectory
    {
        public FixedNodeDataDirectory(string root)
        {
            Root = root;
        }

        public string Root { get; }
    }
}
