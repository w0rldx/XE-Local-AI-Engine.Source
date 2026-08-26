namespace XE_Local_AI_Engine.Tests.ContainerSandbox;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The container provider, driven through a fake Docker client.
///     <para>
///         The cases that matter most here are the ones a real daemon cannot produce on demand: a daemon that quietly
///         ignores a setting. A real Docker Engine applies what it is told, so the branch that refuses an unverifiable
///         container would never execute against one — and a fail-closed control whose failure path never runs is
///         fail-closed on paper only. The <c>SettingsMutator</c> seam exists for exactly these.
///     </para>
/// </summary>
public sealed class DockerSandboxRuntimeProviderTests
{
    private static readonly DateTimeOffset FixedNow = new(year: 2026, month: 7, day: 29, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    [Test]
    public void Capabilities_AdvertiseOnlyWhatTheProviderVerifies()
    {
        var (provider, _, _) = CreateProvider();

        var capabilities = provider.Capabilities;

        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy));
        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsResourceLimits));
        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsReadOnlyMounts));
        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsTrustedHostWorkspace));
        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsCopyOut));
        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsCommandCancellation));
        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsAttach));
        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsKill));

        // Served, but not through Docker's archive endpoint: that answers 400 "container rootfs is marked read-only"
        // against a read-only-rootfs container regardless of destination, and hardening requires that rootfs. The write
        // goes to the host side of the workspace bind mount instead, which needs no archive endpoint at all.
        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsCopyInto));
    }

    [Test]
    public async Task CreateOrAttachAsync_WhenTheDaemonAppliedEverything_ReturnsAVerifiedHandle()
    {
        var (provider, client, workspace) = CreateProvider();

        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace));

        AssertEx.Equal(DockerSandboxRuntimeProvider.Name, handle.ProviderName);
        AssertEx.NotNullOrEmpty(handle.SandboxId);
        AssertEx.Equal(FixedNow, handle.CreatedAt);
        AssertEx.Equal(expected: 1, client.CreatedContainerIds.Count);
        AssertEx.Empty(client.RemovedContainerIds);
    }

    [Test]
    public async Task CreateOrAttachAsync_WhenTheDaemonSilentlyDroppedTheCapabilityDrop_RefusesAndRemovesTheContainer()
    {
        var (provider, client, workspace) = CreateProvider();
        client.SettingsMutator = settings => settings with
        {
            CapabilitiesDropped = []
        };

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => provider.CreateOrAttachAsync(CreateRequest(workspace)));

        AssertEx.Contains(exception.Message, "capability drop");
        // Fail-closed is not only "do not return the handle": the container exists and is running, and leaving it
        // would leak an unverified container the caller never learned about.
        AssertEx.Equal(expected: 1, client.CreatedContainerIds.Count);
        AssertEx.Contains(client.RemovedContainerIds, client.CreatedContainerIds[0]);
    }

    [Test]
    public async Task CreateOrAttachAsync_WhenTheRootFilesystemCameBackWritable_Refuses()
    {
        var (provider, client, workspace) = CreateProvider();
        client.SettingsMutator = settings => settings with
        {
            ReadOnlyRootFilesystem = false
        };

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => provider.CreateOrAttachAsync(CreateRequest(workspace)));

        AssertEx.Contains(exception.Message, "read-only root filesystem");
        AssertEx.Contains(client.RemovedContainerIds, client.CreatedContainerIds[0]);
    }

    [Test]
    public async Task CreateOrAttachAsync_WhenTheResourceCeilingsWereIgnored_Refuses()
    {
        var (provider, client, workspace) = CreateProvider();
        client.SettingsMutator = settings => settings with
        {
            MemoryBytes = 0,
            NanoCpus = 0,
            PidsLimit = 0
        };

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => provider.CreateOrAttachAsync(CreateRequest(workspace)));

        AssertEx.Contains(exception.Message, "memory limit");
        AssertEx.Contains(exception.Message, "CPU limit");
        AssertEx.Contains(exception.Message, "PID limit");
    }

    [Test]
    public async Task CreateOrAttachAsync_WhenTheContainerCameBackOnTheHostNetwork_Refuses()
    {
        var (provider, client, workspace) = CreateProvider();
        client.SettingsMutator = settings => settings with
        {
            NetworkMode = "host"
        };

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => provider.CreateOrAttachAsync(CreateRequest(workspace)));

        AssertEx.Contains(exception.Message, "network mode");
        AssertEx.Contains(client.RemovedContainerIds, client.CreatedContainerIds[0]);
    }

    [Test]
    public async Task CreateOrAttachAsync_WhenAnUnrequestedMountAppeared_Refuses()
    {
        var (provider, client, workspace) = CreateProvider();
        client.SettingsMutator = settings => settings with
        {
            Mounts =
            [
                .. settings.Mounts,
                new DockerBindMount
                {
                    HostPath = "/var/run/docker.sock",
                    ContainerPath = "/var/run/docker.sock",
                    ReadOnly = false,
                    Propagation = "private"
                }
            ]
        };

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => provider.CreateOrAttachAsync(CreateRequest(workspace)));

        AssertEx.Contains(exception.Message, "did not request");
    }

    [Test]
    public async Task CreateOrAttachAsync_HonoursTheCallersResourceCeilingsRatherThanSubstitutingItsOwn()
    {
        // The provider advertises SupportsResourceLimits. Advertising that while quietly applying the engine's own
        // numbers is the same silent-ignore the fail-closed contract exists to prevent: the caller would believe it
        // received the ceiling it asked for. Configured defaults here are 512 MB / 2 CPU / 256 PIDs.
        var (provider, client, workspace) = CreateProvider();

        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace) with
        {
            ResourceLimits = new SandboxResourceLimits
            {
                MemoryMb = 128,
                CpuCount = 0.5,
                PidsLimit = 64
            }
        });

        var settings = await client.InspectContainerAsync(client.CreatedContainerIds[0]);
        AssertEx.Equal(expected: 128L * 1024 * 1024, settings.MemoryBytes);
        AssertEx.Equal(expected: 500_000_000L, settings.NanoCpus);
        AssertEx.Equal(expected: 64L, settings.PidsLimit);
        AssertEx.NotNullOrEmpty(handle.SandboxId);
    }

    [Test]
    public async Task CreateOrAttachAsync_WhenTheCallerStatesNoCeiling_FallsBackToTheConfiguredDefault()
    {
        var (provider, client, workspace) = CreateProvider();

        await provider.CreateOrAttachAsync(CreateRequest(workspace) with
        {
            ResourceLimits = new SandboxResourceLimits
            {
                MemoryMb = 128
            }
        });

        var settings = await client.InspectContainerAsync(client.CreatedContainerIds[0]);
        AssertEx.Equal(expected: 128L * 1024 * 1024, settings.MemoryBytes);
        // Untouched fields keep the engine default rather than becoming unlimited.
        AssertEx.Equal(expected: 2_000_000_000L, settings.NanoCpus);
        AssertEx.Equal(expected: 256L, settings.PidsLimit);
    }

    [Test]
    public async Task CreateOrAttachAsync_WhenARestrictedEgressAllowListIsRequested_RefusesBeforeCreatingAnything()
    {
        var (provider, client, workspace) = CreateProvider();

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => provider.CreateOrAttachAsync(CreateRequest(workspace) with
        {
            NetworkPolicy = SandboxNetworkPolicy.Restricted
        }));

        AssertEx.Contains(exception.Message, "no mechanism");
        // Nothing was created. A container that should never have existed is not a thing the caller should have to
        // reason about afterwards.
        AssertEx.Empty(client.CreatedContainerIds);
    }

    [Test]
    public async Task CreateOrAttachAsync_WhenUnrestrictedEgressIsRequested_ServesItOnTheDefaultBridge()
    {
        // Development Mode asks for this today: its `dotnet restore` needs the network until a restricted package proxy
        // machinery exists, so a provider that served only `None` could never be switched on for it at all.
        var (provider, client, workspace) = CreateProvider();

        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace) with
        {
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted
        });

        var settings = await client.InspectContainerAsync(client.CreatedContainerIds[0]);
        AssertEx.Equal("bridge", settings.NetworkMode);
        AssertEx.NotNullOrEmpty(handle.SandboxId);
    }

    [Test]
    public async Task CreateOrAttachAsync_WhenNoneIsRequested_StillGetsAnEmptyNetworkNamespace()
    {
        // The other half of the pair: widening to serve Unrestricted must not have quietly widened the default.
        var (provider, client, workspace) = CreateProvider();

        await provider.CreateOrAttachAsync(CreateRequest(workspace));

        var settings = await client.InspectContainerAsync(client.CreatedContainerIds[0]);
        AssertEx.Equal("none", settings.NetworkMode);
    }

    [Test]
    public async Task CreateOrAttachAsync_WithoutATrustedHostWorkspace_Refuses()
    {
        var (provider, client, _) = CreateProvider();

        await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = AttachKey(),
            RuntimeProfile = "development",
            NetworkPolicy = SandboxNetworkPolicy.None
        }));

        AssertEx.Empty(client.CreatedContainerIds);
    }

    [Test]
    public async Task ResolveIdentity_WhenTheEngineItselfRunsAsRoot_RefusesRatherThanInheritingRoot()
    {
        // The fail-open this guard exists for: an engine running as root would otherwise silently produce a root
        // container, which satisfies "explicit UID" and violates "non-root".
        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => Task.FromResult(DockerSandboxRuntimeProvider.ResolveIdentity(DockerSandboxHardeningTests.Options() with
            {
                UserId = null,
                GroupId = null
            },
            daemonIsRootless: false,
            () => 0,
            () => 0)));

        AssertEx.Contains(exception.Message, "host root");
    }

    [Test]
    public void ResolveIdentity_WhenUnset_TakesTheEngineProcessOwnIdentifiers()
    {
        var identity = DockerSandboxRuntimeProvider.ResolveIdentity(DockerSandboxHardeningTests.Options() with
            {
                UserId = null,
                GroupId = null
            },
            daemonIsRootless: false,
            () => 1234,
            () => 5678);

        AssertEx.Equal("1234:5678", identity.UserSpecification);
    }

    [Test]
    public void ResolveIdentity_AgainstARootlessDaemon_TakesUidZeroRatherThanTheEngineIdentifiers()
    {
        // The inversion, and the reason the hardening contract's flat "non-root" rule could not stand. A rootless daemon maps container
        // uid 0 to the invoking user and container uid N>0 into the subordinate range, so the engine's own 1000 names
        // a host account (100999 with /etc/subuid = ...:100000:65536) that owns nothing of ours. Measured on Engine
        // 29.6.1 rootless: --user 1000:1000 could not touch a file in the engine-generated workspace mount at all.
        var identity = DockerSandboxRuntimeProvider.ResolveIdentity(DockerSandboxHardeningTests.Options() with
            {
                UserId = null,
                GroupId = null
            },
            daemonIsRootless: true,
            () => 1000,
            () => 1000);

        AssertEx.Equal("0:0", identity.UserSpecification);
    }

    [Test]
    public void ResolveIdentity_AnExplicitOperatorIdentityWinsOverEitherDefault()
    {
        // A daemon may map identities in a way neither rule describes, so the operator keeps the last word.
        var rootless = DockerSandboxRuntimeProvider.ResolveIdentity(DockerSandboxHardeningTests.Options() with
            {
                UserId = 4242,
                GroupId = 4242
            },
            daemonIsRootless: true,
            () => 1000,
            () => 1000);
        var rootful = DockerSandboxRuntimeProvider.ResolveIdentity(DockerSandboxHardeningTests.Options() with
            {
                UserId = 4242,
                GroupId = 4242
            },
            daemonIsRootless: false,
            () => 1000,
            () => 1000);

        AssertEx.Equal("4242:4242", rootless.UserSpecification);
        AssertEx.Equal("4242:4242", rootful.UserSpecification);
    }

    [Test]
    public async Task ResolveIdentity_UidZeroAgainstARootfulDaemon_IsStillRefusedEvenWhenConfigured()
    {
        // The relaxation is scoped to a daemon that reports itself rootless, and nothing else. On a rootful daemon an
        // in-container id maps straight through, so 0 there is host root.
        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => Task.FromResult(DockerSandboxRuntimeProvider.ResolveIdentity(DockerSandboxHardeningTests.Options() with
            {
                UserId = 0,
                GroupId = 0
            },
            daemonIsRootless: false,
            () => 1000,
            () => 1000)));

        AssertEx.Contains(exception.Message, "host root");
    }

    [Test]
    public async Task ExecuteAsync_ReturnsTheCommandOutcome()
    {
        var (provider, client, workspace) = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace));
        client.RegisterCommand("git status", exitCode: 0, "clean");

        var result = await provider.ExecuteAsync(handle,
            new SandboxCommandRequest
            {
                ExecutionId = "exec-1",
                Executable = "git",
                Arguments = ["status"]
            });

        AssertEx.Equal(expected: 0, result.ExitCode);
        AssertEx.Equal("clean", result.StandardOutput);
        AssertEx.True(result.Completed);
    }

    [Test]
    public async Task CopyIntoAsync_WritesThroughTheWorkspaceMountOnTheHostSide()
    {
        // write_file is built on this. While it threw, Development Mode's write_file could not work at all.
        var (provider, _, workspace) = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace));
        var source = Path.Combine(workspace, "..", Guid.NewGuid().ToString("N") + ".source");
        await File.WriteAllTextAsync(source, "written-by-the-engine");

        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = source,
            DestinationPath = "/nested/a.txt"
        });

        // The HOST path behind the mount, not the container path: the mount source is where the bytes go.
        AssertEx.Equal("written-by-the-engine", await File.ReadAllTextAsync(Path.Combine(workspace, "nested", "a.txt")));
        File.Delete(source);
    }

    [Test]
    public async Task CopyIntoAsync_WhenTheDestinationEscapesTheWorkspace_Refuses()
    {
        var (provider, _, workspace) = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace));
        var source = Path.Combine(workspace, "..", Guid.NewGuid().ToString("N") + ".source");
        await File.WriteAllTextAsync(source, "x");

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = source,
            DestinationPath = "/../escaped.txt"
        }));

        AssertEx.False(File.Exists(Path.Combine(workspace, "..", "escaped.txt")));
        File.Delete(source);
    }

    [Test]
    public async Task CopyIntoAsync_WhenAComponentOfTheDestinationIsASymlink_Refuses()
    {
        // A command inside the container can plant one, and it is the HOST that resolves it — so an unguarded write
        // would let the sandbox choose where the engine writes.
        SymlinkSupport.EnsureSupported();

        var (provider, _, workspace) = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace));
        var outside = Path.Combine(workspace, "..", "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(workspace, "escape"), outside);
        var source = Path.Combine(workspace, "..", Guid.NewGuid().ToString("N") + ".source");
        await File.WriteAllTextAsync(source, "x");

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = source,
            DestinationPath = "/escape/planted.txt"
        }));

        AssertEx.False(File.Exists(Path.Combine(outside, "planted.txt")));
        File.Delete(source);
    }

    /// <summary>
    ///     The junction twin of <see cref="CopyIntoAsync_WhenAComponentOfTheDestinationIsASymlink_Refuses" />, which
    ///     skips on a stock Windows box for want of symbolic-link privilege. Junctions need none, so this proves the
    ///     destination-component guard where the symbolic-link test cannot run.
    /// </summary>
    [Test]
    public async Task CopyIntoAsync_WhenAComponentOfTheDestinationIsAJunction_Refuses()
    {
        JunctionSupport.EnsureSupported();

        var (provider, _, workspace) = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace));
        var outside = Path.Combine(workspace, "..", "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        AssertEx.True(JunctionSupport.TryCreate(Path.Combine(workspace, "escape"), outside),
            "the fixture must be able to plant a junction once EnsureSupported has passed");
        var source = Path.Combine(workspace, "..", Guid.NewGuid().ToString("N") + ".source");
        await File.WriteAllTextAsync(source, "x");

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = source,
            DestinationPath = "/escape/planted.txt"
        }));

        AssertEx.False(File.Exists(Path.Combine(outside, "planted.txt")));
        File.Delete(source);
    }

    [Test]
    public async Task ReadFileAsync_WhenTheFileExceedsTheBound_Rejects()
    {
        var (provider, client, workspace) = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace));
        // Note the mapping: the sandbox path "/big.txt" is the workspace root, which the container sees at /workspace.
        client.RegisterCommand("cat /workspace/big.txt", exitCode: 0, new string('x', count: 128));

        await AssertEx.ThrowsAsync<InvalidDataException>(() => provider.ReadFileAsync(handle, "/big.txt", maxBytes: 64));
    }

    [Test]
    public async Task ReadFileAsync_MapsTheSandboxPathOntoTheWorkspaceMountRatherThanTheContainerRoot()
    {
        var (provider, client, workspace) = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace));
        client.RegisterCommand("cat /workspace/src/a.txt", exitCode: 0, "mapped");

        AssertEx.Equal("mapped", await provider.ReadFileAsync(handle, "/src/a.txt", maxBytes: 4096));
    }

    [Test]
    public async Task ReadFileAsync_WhenTheSandboxPathEscapesTheWorkspace_Refuses()
    {
        var (provider, _, workspace) = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace));

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => provider.ReadFileAsync(handle, "/../../etc/shadow", maxBytes: 4096));
    }

    [Test]
    public async Task KillAsync_RemovesTheContainerAndInvalidatesTheHandle()
    {
        var (provider, client, workspace) = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace));

        await provider.KillAsync(handle);

        AssertEx.Contains(client.RemovedContainerIds, client.CreatedContainerIds[0]);
        await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(() => provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "e",
            Executable = "git"
        }));
    }

    [Test]
    public async Task CreateOrAttachAsync_WhenTheSameKeyReturns_ReusesTheContainer()
    {
        var (provider, client, workspace) = CreateProvider();

        var first = await provider.CreateOrAttachAsync(CreateRequest(workspace));
        var second = await provider.CreateOrAttachAsync(CreateRequest(workspace));

        AssertEx.Equal(first.SandboxId, second.SandboxId);
        AssertEx.Equal(expected: 1, client.CreatedContainerIds.Count);
    }

    [Test]
    public async Task ExecuteAsync_MapsTheWorkingDirectoryOntoTheWorkspaceMount()
    {
        // The defect this pins: Development Mode passes the literal "/" for EVERY command. Forwarded unmapped, that
        // reaches the wire as WorkingDir="/" and every build, test and git command runs in the container's root
        // instead of the repository — where it finds no repository at all.
        var (provider, client, workspace) = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace));

        await provider.ExecuteAsync(handle,
            new SandboxCommandRequest
            {
                ExecutionId = "exec-root",
                Executable = "git",
                Arguments = ["status"],
                WorkingDirectory = "/"
            });
        await provider.ExecuteAsync(handle,
            new SandboxCommandRequest
            {
                ExecutionId = "exec-nested",
                Executable = "git",
                Arguments = ["status"],
                WorkingDirectory = "/src/app"
            });

        var working = client.ExecutedRequests
                            .Where(request => string.Equals(request.Executable, "git", StringComparison.Ordinal))
                            .Select(request => request.WorkingDirectory)
                            .ToArray();

        AssertEx.Equal("/workspace|/workspace/src/app", string.Join('|', working));
    }

    [Test]
    public async Task ExecuteAsync_WhenNoWorkingDirectoryIsGiven_UsesTheWorkspaceMount()
    {
        var (provider, client, workspace) = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace));

        await provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "exec-1",
            Executable = "git",
            Arguments = ["status"]
        });

        AssertEx.Equal("/workspace",
            client.ExecutedRequests.Single(request => string.Equals(request.Executable, "git", StringComparison.Ordinal)).WorkingDirectory);
    }

    [Test]
    public async Task ExecuteAsync_WhenTheWorkingDirectoryEscapesTheWorkspace_Refuses()
    {
        var (provider, _, workspace) = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace));

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => provider.ExecuteAsync(handle,
            new SandboxCommandRequest
            {
                ExecutionId = "exec-1",
                Executable = "git",
                WorkingDirectory = "/../../"
            }));
    }

    [Test]
    public async Task ExecuteAsync_CarriesStandardInputThroughToTheClient()
    {
        // apply_patch pipes the patch to `git apply -`. Dropped, git reads EOF from an unattached stdin and EXITS 0,
        // so the tool reports success having applied nothing — a false green rather than a failure.
        var (provider, client, workspace) = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace));

        await provider.ExecuteAsync(handle,
            new SandboxCommandRequest
            {
                ExecutionId = "exec-1",
                Executable = "git",
                Arguments = ["apply", "-"],
                StandardInput = "diff --git a/a.txt b/a.txt\n"
            });

        var executed = client.ExecutedRequests.Single(request => string.Equals(request.Executable, "git", StringComparison.Ordinal));
        AssertEx.Equal("diff --git a/a.txt b/a.txt\n", executed.StandardInput);
    }

    [Test]
    public async Task CreateOrAttachAsync_WhenTheContainerCannotWriteThroughTheWorkspaceMount_RefusesAndRemovesTheContainer()
    {
        // The failure inspect cannot see. Every hardening guarantee reads back perfectly here — the daemon applied exactly
        // what it was asked for — and the container still cannot put a byte in its own workspace, which is what a
        // wrong UID mapping looks like from the outside.
        var (provider, client, workspace) = CreateProvider();
        client.WritesThroughBindMounts = false;

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => provider.CreateOrAttachAsync(CreateRequest(workspace)));

        AssertEx.Contains(exception.Message, "workspace mount");
        AssertEx.Equal(expected: 1, client.CreatedContainerIds.Count);
        AssertEx.Contains(client.RemovedContainerIds, client.CreatedContainerIds[0]);
    }

    [Test]
    public async Task CreateOrAttachAsync_LeavesNoProbeFileBehindInTheWorkspace()
    {
        var (provider, _, workspace) = CreateProvider();

        await provider.CreateOrAttachAsync(CreateRequest(workspace));

        AssertEx.Empty(Directory.GetFileSystemEntries(workspace));
    }

    [Test]
    public void DescribeWorkspaceMappingFailure_WhenTheProbeOwnerIsNotTheEngine_NamesTheMismatch()
    {
        // The rootless mis-mapping in numbers: container uid 1000 lands host-side as 100999 (subuid base 100000), so
        // the engine at uid 1000 cannot modify what its own sandbox writes. No inspect can report this — the daemon
        // echoes back the uid it was ASKED for and knows nothing about what that uid maps to.
        var failure = DockerSandboxRuntimeProvider.DescribeWorkspaceMappingFailure(containerWroteTheProbe: true,
            probeVisibleOnHost: true,
            engineUserId: 1000u,
            probeOwnerUserId: 100_999u);

        AssertEx.Contains(AssertEx.NotNull(failure), "100999");
        AssertEx.Contains(failure, "1000");
    }

    [Test]
    public void DescribeWorkspaceMappingFailure_WhenTheOwnerMatches_ReportsNothing()
    {
        AssertEx.Null(DockerSandboxRuntimeProvider.DescribeWorkspaceMappingFailure(containerWroteTheProbe: true,
            probeVisibleOnHost: true,
            engineUserId: 1000u,
            probeOwnerUserId: 1000u));
    }

    [Test]
    public void DescribeWorkspaceMappingFailure_WhenTheOwnerCannotBeRead_FailsClosed()
    {
        // Refused rather than assumed: an unreadable owner is not evidence of a correct mapping.
        AssertEx.NotNull(DockerSandboxRuntimeProvider.DescribeWorkspaceMappingFailure(containerWroteTheProbe: true,
            probeVisibleOnHost: true,
            engineUserId: 1000u,
            probeOwnerUserId: null));
    }

    [Test]
    public void DescribeWorkspaceMappingFailure_WhenTheProbeIsInvisibleOnTheHost_NamesTheMountRatherThanTheOwner()
    {
        AssertEx.Contains(AssertEx.NotNull(DockerSandboxRuntimeProvider.DescribeWorkspaceMappingFailure(containerWroteTheProbe: true,
                probeVisibleOnHost: false,
                engineUserId: 1000u,
                probeOwnerUserId: null)),
            "not present on the host");
    }

    [Test]
    public void DescribeWorkspaceMappingFailure_OnAnEngineHostWithNoUnixOwnership_StopsAtTheWriteThroughChecks()
    {
        // A Windows engine has no host UID to compare against, and the write-through checks have already established
        // the property that matters. Claiming nothing further beats guessing.
        AssertEx.Null(DockerSandboxRuntimeProvider.DescribeWorkspaceMappingFailure(containerWroteTheProbe: true,
            probeVisibleOnHost: true,
            engineUserId: null,
            probeOwnerUserId: null));
    }

    private static (DockerSandboxRuntimeProvider Provider, FakeDockerRuntimeClient Client, string Workspace) CreateProvider()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "xe-container-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        var client = new FakeDockerRuntimeClient(new DockerDaemonEndpoint(new Uri("unix:///fake.sock"),
            DockerDaemonEndpointSource.Configuration));

        var provider = new DockerSandboxRuntimeProvider(new StaticOptionsMonitor<ContainerSandboxOptions>(DockerSandboxHardeningTests.Options()),
            new StubDockerRuntimeClientFactory(client),
            new FakeNodeDataDirectory(workspace),
            new FixedTimeProvider(FixedNow),
            NullLogger<DockerSandboxRuntimeProvider>.Instance);

        return (provider, client, workspace);
    }

    private static SandboxCreateRequest CreateRequest(string workspaceRoot)
    {
        return new SandboxCreateRequest
        {
            AttachKey = AttachKey(),
            RuntimeProfile = "development",
            NetworkPolicy = SandboxNetworkPolicy.None,
            TrustedHostWorkspace = new SandboxTrustedHostWorkspace
            {
                RootPath = workspaceRoot
            }
        };
    }

    private static SandboxAttachKey AttachKey()
    {
        return new SandboxAttachKey
        {
            OwnerUserId = "owner-1",
            NodeId = "node-1",
            ProviderName = DockerSandboxRuntimeProvider.Name,
            RuntimeProfile = "development",
            ManifestVersion = 1
        };
    }

    /// <summary>Hands the provider the one fake client the test holds, so assertions read the same instance.</summary>
    private sealed class StubDockerRuntimeClientFactory : IDockerRuntimeClientFactory
    {
        private readonly FakeDockerRuntimeClient _client;

        public StubDockerRuntimeClientFactory(FakeDockerRuntimeClient client)
        {
            _client = client;
        }

        public IDockerRuntimeClient Create(DockerDaemonEndpoint endpoint)
        {
            return _client;
        }
    }
}
