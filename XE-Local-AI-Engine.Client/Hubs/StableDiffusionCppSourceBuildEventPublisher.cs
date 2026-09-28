namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

internal sealed class StableDiffusionCppSourceBuildEventPublisher : IStableDiffusionCppSourceBuildEventPublisher
{
    private readonly IHubContext<StableDiffusionCppSourceBuildHub> _hubContext;

    public StableDiffusionCppSourceBuildEventPublisher(IHubContext<StableDiffusionCppSourceBuildHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PublishStatusAsync(StableDiffusionCppSourceBuildStatusEvent statusEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(statusEvent);
        return _hubContext.Clients.All.SendAsync(StableDiffusionCppSourceBuildHubEvents.StatusChanged,
            ToHubMessage(statusEvent), ct);
    }

    private static StableDiffusionCppSourceBuildStatusHubMessage ToHubMessage(StableDiffusionCppSourceBuildStatusEvent statusEvent)
    {
        return new StableDiffusionCppSourceBuildStatusHubMessage
        {
            Phase = statusEvent.Phase.ToWireString(),
            AppendedLogLines = statusEvent.AppendedLogLines,
            AppendedLogStartSequence = statusEvent.AppendedLogStartSequence,
            Terminal = statusEvent.Terminal,
            SanitizedError = statusEvent.SanitizedError,
            CurrentBuild = statusEvent.CurrentBuild is null ? null : ToDescriptorHubMessage(statusEvent.CurrentBuild)
        };
    }

    private static StableDiffusionCppSourceBuildDescriptorHubMessage ToDescriptorHubMessage(StableDiffusionCppSourceBuildDescriptor descriptor)
    {
        var response = descriptor.ToResponse();
        return new StableDiffusionCppSourceBuildDescriptorHubMessage
        {
            BuildId = response.BuildId,
            Backend = response.Backend switch
            {
                StableDiffusionCppSourceBackendDto.Cpu => "cpu",
                StableDiffusionCppSourceBackendDto.Vulkan => "vulkan",
                StableDiffusionCppSourceBackendDto.Cuda => "cuda",
                _ => throw new ArgumentOutOfRangeException(nameof(descriptor), descriptor.Backend, "Unknown source-build backend.")
            },
            Source = response.Source switch
            {
                StableDiffusionCppSourceSelectionDto.Official => "official",
                StableDiffusionCppSourceSelectionDto.Custom => "custom",
                _ => throw new ArgumentOutOfRangeException(nameof(descriptor), descriptor.Source, "Unknown source selection.")
            },
            Repository = response.Repository,
            RevisionMode = response.RevisionMode switch
            {
                StableDiffusionCppSourceRevisionModeDto.EnginePinned => "enginePinned",
                StableDiffusionCppSourceRevisionModeDto.DefaultBranch => "defaultBranch",
                StableDiffusionCppSourceRevisionModeDto.ExplicitCommit => "explicitCommit",
                _ => throw new ArgumentOutOfRangeException(nameof(descriptor), descriptor.RevisionMode, "Unknown source revision mode.")
            },
            RequestedCommit = response.RequestedCommit,
            ResolvedCommit = response.ResolvedCommit
        };
    }
}
