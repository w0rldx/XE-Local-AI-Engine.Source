namespace XE_Local_AI_Engine.Tests.HostAgent;

using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts;
using XE_Local_AI_Engine.HostAgent.Linux.Docker;
using XE_Local_AI_Engine.HostAgent.Linux.Docker.Implementation;
using XE_Local_AI_Engine.HostAgent.Linux.Services;
using XE_Local_AI_Engine.Tests.Testing;
using DockerNetworkMode = XE_Local_AI_Engine.HostAgent.Linux.Docker.SandboxNetworkMode;
using ProtoNetworkMode = XE_Local_AI_Engine.HostAgent.Grpc.Contracts.SandboxNetworkMode;

/// <summary>
///     Handler coverage for <see cref="SandboxRuntimeService" /> (Marker J-local plan §4.2, §9.1) driven entirely by
///     <see cref="FakeDockerRuntimeClient" /> — NO Docker. Asserts spec mapping (limits/network/labels), exec result
///     translation, byte-lossless copy-into → read-file round-trips, best-effort cancel unblocking a blocking exec,
///     and kill invalidation. Also pins the pure resource/network → <c>HostConfig</c> mapping.
/// </summary>
public sealed class SandboxRuntimeServiceTests
{
    private static SandboxAttachKeyMessage AttachKey(string owner = "owner-1", string node = "node-7", int manifest = 5)
    {
        return new SandboxAttachKeyMessage
        {
            OwnerUserId = owner,
            NodeId = node,
            ProviderName = "local-container",
            RuntimeProfile = "dotnet-agent-home",
            ManifestVersion = manifest
        };
    }

    private static (SandboxRuntimeService Service, FakeDockerRuntimeClient Docker) CreateService()
    {
        var docker = new FakeDockerRuntimeClient(TimeProvider.System);
        var service = new SandboxRuntimeService(docker, TimeProvider.System, NullLogger<SandboxRuntimeService>.Instance);
        return (service, docker);
    }

    private static CreateSandboxRequest CreateRequest()
    {
        return new CreateSandboxRequest
        {
            AttachKey = AttachKey(),
            RuntimeProfile = "dotnet-agent-home",
            DefaultImage = "dotnet-agent-home:2026-05-agenthome-mvp",
            Limits = new ResourceLimitsMessage { CpuCount = 2.0, MemoryMb = 4096, PidsLimit = 512 },
            Network = ProtoNetworkMode.None,
            Labels = { ["team"] = "core" }
        };
    }

    // Mirrors SandboxRuntimeService.BuildContainerName's owner-hash component so the Name label can be asserted.
    private static string Sha256Prefix12(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];
    }

    [Test]
    public async Task CreateOrAttachSandbox_RecordsSpecWithLimitsNetworkAndAttachLabels()
    {
        var (service, docker) = CreateService();

        var reply = await service.CreateOrAttachSandbox(CreateRequest(), Context());

        AssertEx.NotNullOrEmpty(reply.SandboxId);
        AssertEx.Equal(5, reply.ManifestVersion);
        AssertEx.Equal(AttachKey(), reply.AttachKey);

        var spec = AssertEx.NotNull(docker.TryGetRecordedSpec(reply.SandboxId));
        AssertEx.Equal("dotnet-agent-home:2026-05-agenthome-mvp", spec.Image);
        AssertEx.Equal(2.0, spec.CpuCount);
        AssertEx.Equal(4096, spec.MemoryMb);
        AssertEx.Equal(512, spec.PidsLimit);
        AssertEx.Equal(DockerNetworkMode.None, spec.NetworkMode);
        AssertEx.Equal("owner-1", spec.Labels[SandboxLabelKeys.Owner]);
        AssertEx.Equal("node-7", spec.Labels[SandboxLabelKeys.Node]);
        AssertEx.Equal("dotnet-agent-home", spec.Labels[SandboxLabelKeys.Profile]);
        AssertEx.Equal("5", spec.Labels[SandboxLabelKeys.Manifest]);
        AssertEx.Equal("c0re-agent-home-node-7-" + Sha256Prefix12("owner-1"), spec.Labels[SandboxLabelKeys.Name]);
        AssertEx.Equal("core", spec.Labels["team"]);
        // The hardened create runs as the image's non-root account (FIX 3).
        AssertEx.Equal("agent", spec.User);
    }

    [Test]
    public async Task CreateOrAttachSandbox_WhenCalledTwiceForSameKey_ReusesContainer()
    {
        var (service, _) = CreateService();

        var first = await service.CreateOrAttachSandbox(CreateRequest(), Context());
        var second = await service.CreateOrAttachSandbox(CreateRequest(), Context());

        AssertEx.Equal(first.SandboxId, second.SandboxId);
    }

    [Test]
    public async Task CreateOrAttachSandbox_WhenImageMissing_ThrowsInvalidArgument()
    {
        var (service, _) = CreateService();
        var request = CreateRequest();
        request.DefaultImage = string.Empty;

        var exception = await AssertEx.ThrowsAsync<RpcException>(() => service.CreateOrAttachSandbox(request, Context()));

        AssertEx.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    [Test]
    public async Task ConnectSandbox_WhenNoLiveSandbox_ThrowsFailedPrecondition()
    {
        var (service, _) = CreateService();

        var exception = await AssertEx.ThrowsAsync<RpcException>(() =>
            service.ConnectSandbox(new ConnectSandboxRequest { AttachKey = AttachKey() }, Context()));

        AssertEx.Equal(StatusCode.FailedPrecondition, exception.StatusCode);
    }

    [Test]
    public async Task ConnectSandbox_WhenLiveSandboxExists_ReturnsHandle()
    {
        var (service, _) = CreateService();
        var created = await service.CreateOrAttachSandbox(CreateRequest(), Context());

        var connected = await service.ConnectSandbox(new ConnectSandboxRequest { AttachKey = AttachKey() }, Context());

        AssertEx.Equal(created.SandboxId, connected.SandboxId);
    }

    [Test]
    public async Task CreateOrAttachSandbox_WhenExistingContainerHasDifferentProfile_RejectsAttach()
    {
        // Same owner + node → same deterministic container name → the existing container is FOUND, but its profile
        // label differs from the new request's. A name match alone must NOT reuse it (§6.2.1 rule 15).
        var (service, _) = CreateService();
        await service.CreateOrAttachSandbox(CreateRequest(), Context());

        var mismatched = CreateRequest();
        mismatched.AttachKey.RuntimeProfile = "python-agent-home";

        var exception = await AssertEx.ThrowsAsync<RpcException>(() => service.CreateOrAttachSandbox(mismatched, Context()));

        AssertEx.Equal(StatusCode.FailedPrecondition, exception.StatusCode);
    }

    [Test]
    public async Task CreateOrAttachSandbox_WhenExistingContainerHasDifferentManifest_RejectsAttach()
    {
        var (service, _) = CreateService();
        await service.CreateOrAttachSandbox(CreateRequest(), Context());

        var mismatched = CreateRequest();
        mismatched.AttachKey.ManifestVersion = 99;

        var exception = await AssertEx.ThrowsAsync<RpcException>(() => service.CreateOrAttachSandbox(mismatched, Context()));

        AssertEx.Equal(StatusCode.FailedPrecondition, exception.StatusCode);
    }

    [Test]
    public async Task ConnectSandbox_WhenExistingContainerHasDifferentProfile_RejectsAttach()
    {
        var (service, _) = CreateService();
        await service.CreateOrAttachSandbox(CreateRequest(), Context());

        var mismatched = AttachKey();
        mismatched.RuntimeProfile = "python-agent-home";

        var exception = await AssertEx.ThrowsAsync<RpcException>(() =>
            service.ConnectSandbox(new ConnectSandboxRequest { AttachKey = mismatched }, Context()));

        AssertEx.Equal(StatusCode.FailedPrecondition, exception.StatusCode);
    }


    [Test]
    public async Task ExecuteCommand_ReturnsScriptedExitCodeAndOutput()
    {
        var (service, docker) = CreateService();
        docker.ScriptExec("dotnet --info", 0, "runtime: 10.0.0");
        var created = await service.CreateOrAttachSandbox(CreateRequest(), Context());

        var reply = await service.ExecuteCommand(new ExecuteCommandRequest
        {
            SandboxId = created.SandboxId,
            ExecutionId = "exec-1",
            Executable = "dotnet",
            Arguments = { "--info" }
        }, Context());

        AssertEx.Equal("exec-1", reply.ExecutionId);
        AssertEx.Equal(0, reply.ExitCode);
        AssertEx.Equal("runtime: 10.0.0", reply.StandardOutput);
        AssertEx.True(reply.Completed);
    }

    [Test]
    public async Task ExecuteCommand_WhenSandboxMissing_ThrowsFailedPrecondition()
    {
        var (service, _) = CreateService();

        var exception = await AssertEx.ThrowsAsync<RpcException>(() => service.ExecuteCommand(new ExecuteCommandRequest
        {
            SandboxId = "nonexistent",
            ExecutionId = "exec-1",
            Executable = "git",
            Arguments = { "status" }
        }, Context()));

        AssertEx.Equal(StatusCode.FailedPrecondition, exception.StatusCode);
    }

    [Test]
    public async Task CopyInto_ThenReadFile_RoundTripsRawBytes()
    {
        var (service, _) = CreateService();
        var created = await service.CreateOrAttachSandbox(CreateRequest(), Context());
        var content = new byte[] { 0xde, 0xad, 0xbe, 0xef, 0x00, 0xff };

        await service.CopyInto(new CopyIntoRequest
        {
            SandboxId = created.SandboxId,
            DestinationPath = "/agent-home/workspace/selected/image.bin",
            Content = ByteString.CopyFrom(content),
            FileMode = 0b110_100_100
        }, Context());

        var read = await service.ReadFile(new ReadFileRequest
        {
            SandboxId = created.SandboxId,
            SandboxPath = "/agent-home/workspace/selected/image.bin"
        }, Context());

        AssertEx.True(content.AsSpan().SequenceEqual(read.Content.Span), "read-back bytes must equal what was copied in.");
    }

    [Test]
    public async Task CopyOut_ReturnsBytesPreviouslyCopiedIn()
    {
        var (service, _) = CreateService();
        var created = await service.CreateOrAttachSandbox(CreateRequest(), Context());
        var content = new byte[] { 0x01, 0x02, 0x03 };

        await service.CopyInto(new CopyIntoRequest
        {
            SandboxId = created.SandboxId,
            DestinationPath = "/agent-home/out.patch",
            Content = ByteString.CopyFrom(content)
        }, Context());

        var read = await service.CopyOut(new CopyOutRequest
        {
            SandboxId = created.SandboxId,
            SourcePath = "/agent-home/out.patch"
        }, Context());

        AssertEx.True(content.AsSpan().SequenceEqual(read.Content.Span), "copy-out bytes must equal the copied-in content.");
    }

    [Test]
    public async Task ReadFile_WhenPathMissing_ThrowsNotFound()
    {
        var (service, _) = CreateService();
        var created = await service.CreateOrAttachSandbox(CreateRequest(), Context());

        var exception = await AssertEx.ThrowsAsync<RpcException>(() => service.ReadFile(new ReadFileRequest
        {
            SandboxId = created.SandboxId,
            SandboxPath = "/agent-home/missing.txt"
        }, Context()));

        AssertEx.Equal(StatusCode.NotFound, exception.StatusCode);
    }

    [Test]
    public async Task ExecuteCommand_WhenCancelled_ReturnsNonCompletedResult()
    {
        var (service, docker) = CreateService();
        docker.ScriptBlockingExec("sleep 1000");
        var created = await service.CreateOrAttachSandbox(CreateRequest(), Context());

        using var cts = new CancellationTokenSource();
        var execTask = service.ExecuteCommand(new ExecuteCommandRequest
        {
            SandboxId = created.SandboxId,
            ExecutionId = "exec-block",
            Executable = "sleep",
            Arguments = { "1000" }
        }, Context(cts.Token));

        await cts.CancelAsync();
        var reply = await execTask;

        AssertEx.False(reply.Completed, "a cancelled exec must report Completed = false.");
        AssertEx.Equal(-1, reply.ExitCode);
    }

    [Test]
    public async Task KillSandbox_RemovesSandboxSoLaterOperationsFail()
    {
        var (service, _) = CreateService();
        var created = await service.CreateOrAttachSandbox(CreateRequest(), Context());

        await service.KillSandbox(new KillSandboxRequest { SandboxId = created.SandboxId }, Context());

        var exception = await AssertEx.ThrowsAsync<RpcException>(() => service.ReadFile(new ReadFileRequest
        {
            SandboxId = created.SandboxId,
            SandboxPath = "/agent-home/anything"
        }, Context()));

        AssertEx.Equal(StatusCode.FailedPrecondition, exception.StatusCode);
    }

    [Test]
    public async Task KillSandbox_WhenAlreadyGone_IsIdempotentSuccess()
    {
        var (service, _) = CreateService();

        // No sandbox was ever created; a kill must not throw (best-effort, plan §6).
        await service.KillSandbox(new KillSandboxRequest { SandboxId = "never-existed" }, Context());
    }

    [Test]
    public async Task CancelCommand_IsNoOpAndNeverThrows()
    {
        var (service, _) = CreateService();

        await service.CancelCommand(new CancelCommandRequest { SandboxId = "s1", ExecutionId = "exec-1" }, Context());
    }

    // --- Pure resource/network → HostConfig mapping (no Docker). ---

    [Test]
    public void BuildSandboxHostConfig_AppliesLimitsAndHardeningDefaults()
    {
        var spec = new SandboxContainerSpec
        {
            Name = "c0re-agent-home-node-7-abc",
            Image = "dotnet-agent-home:2026-05-agenthome-mvp",
            CpuCount = 2.0,
            MemoryMb = 4096,
            PidsLimit = 512,
            NetworkMode = DockerNetworkMode.None
        };

        var hostConfig = DockerRuntimeClient.BuildSandboxHostConfig(spec);

        AssertEx.Equal(4096L * 1024 * 1024, hostConfig.Memory);
        AssertEx.Equal(2_000_000_000L, hostConfig.NanoCPUs);
        AssertEx.Equal(512L, hostConfig.PidsLimit ?? -1L);
        AssertEx.Equal("none", hostConfig.NetworkMode);
        AssertEx.Contains(hostConfig.SecurityOpt, "no-new-privileges");
        AssertEx.Contains(hostConfig.CapDrop, "ALL");
        AssertEx.False(hostConfig.AutoRemove);
        AssertEx.False(hostConfig.ReadonlyRootfs);
    }

    [Test]
    public void BuildSandboxHostConfig_WhenLimitsUnset_LeavesThemUnconstrained()
    {
        var spec = new SandboxContainerSpec
        {
            Name = "c0re-agent-home-node-7-abc",
            Image = "dotnet-agent-home:2026-05-agenthome-mvp"
        };

        var hostConfig = DockerRuntimeClient.BuildSandboxHostConfig(spec);

        AssertEx.Equal(0L, hostConfig.Memory);
        AssertEx.Equal(0L, hostConfig.NanoCPUs);
        AssertEx.True(hostConfig.PidsLimit is null, "an unset PidsLimit must stay null (unconstrained).");
        // The hardening posture is unconditional even when no resource limits are requested.
        AssertEx.Equal("none", hostConfig.NetworkMode);
        AssertEx.Contains(hostConfig.CapDrop, "ALL");
    }

    private static ServerCallContext Context(CancellationToken cancellationToken = default)
    {
        return new TestServerCallContext(cancellationToken);
    }

    private sealed class TestServerCallContext : ServerCallContext
    {
        private readonly CancellationToken _cancellationToken;

        public TestServerCallContext(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        protected override string MethodCore => "/xe.hostagent.v1.SandboxControl/Test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "test";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => [];
        protected override CancellationToken CancellationTokenCore => _cancellationToken;
        protected override Metadata ResponseTrailersCore { get; } = [];
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(string.Empty, new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
        {
            throw new NotSupportedException();
        }

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
        {
            return Task.CompletedTask;
        }
    }
}
