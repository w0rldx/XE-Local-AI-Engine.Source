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
            Repository = "https://github.com/example/fork"
        });

        AssertEx.False(result.IsValid);
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
}
