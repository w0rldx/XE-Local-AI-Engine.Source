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

        // Measured, not assumed: Docker answers 400 "container rootfs is marked read-only" to an archive extraction
        // against a read-only-rootfs container regardless of destination, and §3.8 requires that rootfs. So the
        // capability cannot be honoured and is not claimed.
        AssertEx.False(capabilities.HasFlag(SandboxProviderCapabilities.SupportsCopyInto));
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
        client.SettingsMutator = settings => settings with { CapabilitiesDropped = [] };

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(
            () => provider.CreateOrAttachAsync(CreateRequest(workspace)));

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
        client.SettingsMutator = settings => settings with { ReadOnlyRootFilesystem = false };

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(
            () => provider.CreateOrAttachAsync(CreateRequest(workspace)));

        AssertEx.Contains(exception.Message, "read-only root filesystem");
        AssertEx.Contains(client.RemovedContainerIds, client.CreatedContainerIds[0]);
    }

    [Test]
    public async Task CreateOrAttachAsync_WhenTheResourceCeilingsWereIgnored_Refuses()
    {
        var (provider, client, workspace) = CreateProvider();
        client.SettingsMutator = settings => settings with { MemoryBytes = 0, NanoCpus = 0, PidsLimit = 0 };

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(
            () => provider.CreateOrAttachAsync(CreateRequest(workspace)));

        AssertEx.Contains(exception.Message, "memory limit");
        AssertEx.Contains(exception.Message, "CPU limit");
        AssertEx.Contains(exception.Message, "PID limit");
    }

    [Test]
    public async Task CreateOrAttachAsync_WhenTheContainerCameBackOnTheHostNetwork_Refuses()
    {
        var (provider, client, workspace) = CreateProvider();
        client.SettingsMutator = settings => settings with { NetworkMode = "host" };

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(
            () => provider.CreateOrAttachAsync(CreateRequest(workspace)));

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

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(
            () => provider.CreateOrAttachAsync(CreateRequest(workspace)));

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
            ResourceLimits = new SandboxResourceLimits { MemoryMb = 128, CpuCount = 0.5, PidsLimit = 64 }
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
            ResourceLimits = new SandboxResourceLimits { MemoryMb = 128 }
        });

        var settings = await client.InspectContainerAsync(client.CreatedContainerIds[0]);
        AssertEx.Equal(expected: 128L * 1024 * 1024, settings.MemoryBytes);
        // Untouched fields keep the engine default rather than becoming unlimited.
        AssertEx.Equal(expected: 2_000_000_000L, settings.NanoCpus);
        AssertEx.Equal(expected: 256L, settings.PidsLimit);
    }

    [Test]
    public async Task CreateOrAttachAsync_WhenANetworkPolicyOtherThanNoneIsRequested_RefusesBeforeCreatingAnything()
    {
        var (provider, client, workspace) = CreateProvider();

        await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(
            () => provider.CreateOrAttachAsync(CreateRequest(workspace) with { NetworkPolicy = SandboxNetworkPolicy.Unrestricted }));

        // Nothing was created. A container that should never have existed is not a thing the caller should have to
        // reason about afterwards.
        AssertEx.Empty(client.CreatedContainerIds);
    }

    [Test]
    public async Task CreateOrAttachAsync_WithoutATrustedHostWorkspace_Refuses()
    {
        var (provider, client, _) = CreateProvider();

        await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(
            () => provider.CreateOrAttachAsync(new SandboxCreateRequest
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
        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(
            () => Task.FromResult(DockerSandboxRuntimeProvider.ResolveIdentity(
                DockerSandboxHardeningTests.Options() with { UserId = null, GroupId = null },
                () => 0,
                () => 0)));

        AssertEx.Contains(exception.Message, "non-root execution");
    }

    [Test]
    public void ResolveIdentity_WhenUnset_TakesTheEngineProcessOwnIdentifiers()
    {
        var identity = DockerSandboxRuntimeProvider.ResolveIdentity(DockerSandboxHardeningTests.Options() with { UserId = null, GroupId = null },
            () => 1234,
            () => 5678);

        AssertEx.Equal("1234:5678", identity.UserSpecification);
    }

    [Test]
    public async Task ExecuteAsync_ReturnsTheCommandOutcome()
    {
        var (provider, client, workspace) = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace));
        client.RegisterCommand("git status", exitCode: 0, "clean");

        var result = await provider.ExecuteAsync(handle,
            new SandboxCommandRequest { ExecutionId = "exec-1", Executable = "git", Arguments = ["status"] });

        AssertEx.Equal(expected: 0, result.ExitCode);
        AssertEx.Equal("clean", result.StandardOutput);
        AssertEx.True(result.Completed);
    }

    [Test]
    public async Task CopyIntoAsync_IsRejectedRatherThanEmulated()
    {
        var (provider, _, workspace) = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace));

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(
            () => provider.CopyIntoAsync(handle, new SandboxCopyRequest { SourcePath = "/host/a", DestinationPath = "/scratch/a" }));

        AssertEx.Contains(exception.Message, "read-only root filesystem");
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
    public async Task KillAsync_RemovesTheContainerAndInvalidatesTheHandle()
    {
        var (provider, client, workspace) = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace));

        await provider.KillAsync(handle);

        AssertEx.Contains(client.RemovedContainerIds, client.CreatedContainerIds[0]);
        await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(
            () => provider.ExecuteAsync(handle, new SandboxCommandRequest { ExecutionId = "e", Executable = "git" }));
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

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(
            () => provider.ReadFileAsync(handle, "/../../etc/shadow", maxBytes: 4096));
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
            new SandboxCommandRequest { ExecutionId = "exec-root", Executable = "git", Arguments = ["status"], WorkingDirectory = "/" });
        await provider.ExecuteAsync(handle,
            new SandboxCommandRequest { ExecutionId = "exec-nested", Executable = "git", Arguments = ["status"], WorkingDirectory = "/src/app" });

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

        await provider.ExecuteAsync(handle, new SandboxCommandRequest { ExecutionId = "exec-1", Executable = "git", Arguments = ["status"] });

        AssertEx.Equal("/workspace",
            client.ExecutedRequests.Single(request => string.Equals(request.Executable, "git", StringComparison.Ordinal)).WorkingDirectory);
    }


    [Test]
    public async Task ExecuteAsync_WhenTheWorkingDirectoryEscapesTheWorkspace_Refuses()
    {
        var (provider, _, workspace) = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(workspace));

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => provider.ExecuteAsync(handle,
            new SandboxCommandRequest { ExecutionId = "exec-1", Executable = "git", WorkingDirectory = "/../../" }));
    }

    private static (DockerSandboxRuntimeProvider Provider, FakeDockerRuntimeClient Client, string Workspace) CreateProvider()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "xe-container-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        var client = new FakeDockerRuntimeClient(new DockerDaemonEndpoint(new Uri("unix:///fake.sock"),
            DockerDaemonEndpointSource.Configuration));

        var provider = new DockerSandboxRuntimeProvider(new StaticOptionsMonitor<ContainerSandboxOptions>(DockerSandboxHardeningTests.Options()),
            new StubDockerRuntimeClientFactory(client),
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
            TrustedHostWorkspace = new SandboxTrustedHostWorkspace { RootPath = workspaceRoot }
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
