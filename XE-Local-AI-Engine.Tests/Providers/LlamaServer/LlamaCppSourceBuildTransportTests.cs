namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using Microsoft.AspNetCore.SignalR;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LlamaCppSourceBuildTransportTests
{
    [Test]
    public void Mapper_PreservesBackendAndRevisionIntent()
    {
        var request = new StartLlamaCppSourceBuildRequest
        {
            Backend = LlamaCppSourceBackendDto.Vulkan,
            Source = LlamaCppSourceSelectionDto.Custom,
            Repository = "https://github.com/example/fork",
            Commit = "ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD",
            AcknowledgeCustomSourceRisk = true
        };

        var normalized = LlamaCppSourceBuildRequestValidation.Normalize(request.ToContract());

        AssertEx.Equal(LlamaCppSourceBackend.Vulkan, normalized.Backend);
        AssertEx.Equal(LlamaCppSourceSelection.Custom, normalized.Source);
        AssertEx.Equal("abcdefabcdefabcdefabcdefabcdefabcdefabcd", normalized.Commit);
    }

    [Test]
    public void Validator_RejectsCustomSourceWithoutRiskAcknowledgement()
    {
        var validator = new StartLlamaCppSourceBuildRequestValidator();
        var result = validator.Validate(new StartLlamaCppSourceBuildRequest
        {
            Backend = LlamaCppSourceBackendDto.Cpu,
            Source = LlamaCppSourceSelectionDto.Custom,
            Repository = "https://github.com/example/fork",
            AcknowledgeCustomSourceRisk = false
        });

        AssertEx.False(result.IsValid);
    }

    [Test]
    public void PrerequisiteValidator_RejectsUndefinedBackend()
    {
        var validator = new GetLlamaCppSourceBuildPrerequisitesRequestValidator();
        var result = validator.Validate(new GetLlamaCppSourceBuildPrerequisitesRequest
        {
            Backend = (LlamaCppSourceBackendDto)99
        });

        AssertEx.False(result.IsValid);
    }

    [Test]
    public async Task Remove_AcquiresLeaseChecksProcessesRemovesAndDisposesInOrder()
    {
        var order = new List<string>();
#pragma warning disable CA2000 // Ownership transfers through the supervisor to TryRemoveAsync, which disposes the lease.
        var lease = new RecordingMutationLease(order);
#pragma warning restore CA2000
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add("acquire");
            return Task.FromResult<ILlamaServerRuntimeMutationLease?>(lease);
        });
        supervisor.CountRunningProcesses().Returns(_ =>
        {
            order.Add("count");
            return 0;
        });
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        binaryManager.RemoveSourceBuildAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add("remove");
            return Task.CompletedTask;
        });

        var (removed, _) = await RemoveLlamaCppSourceBuildEndpoint.TryRemoveAsync(binaryManager, supervisor, CancellationToken.None);

        AssertEx.True(removed);
        AssertEx.Equal("acquire|count|remove|dispose", string.Join('|', order));
    }

    [Test]
    public async Task Remove_WhenLeaseCannotBeAcquired_ReturnsConflictSeamWithoutDeleting()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ILlamaServerRuntimeMutationLease?>(null));
        supervisor.CountRunningProcesses().Returns(1);
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();

        var (removed, runningProcessCount) = await RemoveLlamaCppSourceBuildEndpoint.TryRemoveAsync(binaryManager, supervisor, CancellationToken.None);

        AssertEx.False(removed);
        AssertEx.Equal(expected: 1, runningProcessCount);
        await binaryManager.DidNotReceiveWithAnyArgs().RemoveSourceBuildAsync(default);
    }

    [Test]
    public async Task Publisher_ForwardsOnlyLegacyPinnedCudaToLegacyHub()
    {
        var genericProxy = Substitute.For<IClientProxy>();
        var legacyProxy = Substitute.For<IClientProxy>();
        var genericClients = Substitute.For<IHubClients>();
        var legacyClients = Substitute.For<IHubClients>();
        genericClients.All.Returns(genericProxy);
        legacyClients.All.Returns(legacyProxy);
        var genericHub = Substitute.For<IHubContext<LlamaCppSourceBuildHub>>();
        var legacyHub = Substitute.For<IHubContext<CudaBuildHub>>();
        genericHub.Clients.Returns(genericClients);
        legacyHub.Clients.Returns(legacyClients);
        var publisher = new LlamaCppSourceBuildEventPublisher(genericHub, legacyHub);

        var custom = new LlamaCppSourceBuildDescriptor(GpuVariant.Cpu,
            LlamaCppSourceSelection.Custom,
            "https://github.com/example/fork",
            LlamaCppSourceRevisionMode.DefaultBranch,
            null,
            new string('a', 40));
        await publisher.PublishStatusAsync(new LlamaCppSourceBuildStatusHubEvent("Building", [], false, null, custom));

        await genericProxy.Received(1).SendCoreAsync(LlamaCppSourceBuildHubEvents.StatusChanged, Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
        await legacyProxy.DidNotReceive().SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());

        var legacy = new LlamaCppSourceBuildDescriptor(GpuVariant.Cuda,
            LlamaCppSourceSelection.Official,
            LlamaCppSourceBuildRequestValidation.OfficialRepository,
            LlamaCppSourceRevisionMode.EnginePinned,
            null,
            LlamaCppReleasePins.PinnedSourceCommitSha);
        await publisher.PublishStatusAsync(new LlamaCppSourceBuildStatusHubEvent("Building", ["line"], false, null, legacy));

        await legacyProxy.Received(1).SendCoreAsync(CudaBuildHubEvents.StatusChanged, Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    private sealed class RecordingMutationLease(List<string> order) : ILlamaServerRuntimeMutationLease
    {
        public ValueTask DisposeAsync()
        {
            order.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }
}
