namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

internal sealed class StableDiffusionCppSourceBuildEventPublisher(IHubContext<StableDiffusionCppSourceBuildHub> hubContext) : IStableDiffusionCppSourceBuildEventPublisher
{
    public Task PublishStatusAsync(StableDiffusionCppSourceBuildStatusEvent statusEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(statusEvent);
        return hubContext.Clients.All.SendAsync(StableDiffusionCppSourceBuildEvents.StatusChanged,
            StableDiffusionCppSourceBuildStatusHubMessage.FromContract(statusEvent), ct);
    }
}

internal sealed class StableDiffusionCppSourceBuildStatusHubMessage
{
    public required string Phase { get; init; }
    public required IReadOnlyList<string> AppendedLogLines { get; init; }
    public required long AppendedLogStartSequence { get; init; }
    public required bool Terminal { get; init; }
    public string? SanitizedError { get; init; }
    public StableDiffusionCppSourceBuildDescriptorHubMessage? CurrentBuild { get; init; }

    public static StableDiffusionCppSourceBuildStatusHubMessage FromContract(StableDiffusionCppSourceBuildStatusEvent statusEvent)
    {
        return new StableDiffusionCppSourceBuildStatusHubMessage
        {
            Phase = statusEvent.Phase.ToWireString(),
            AppendedLogLines = statusEvent.AppendedLogLines,
            AppendedLogStartSequence = statusEvent.AppendedLogStartSequence,
            Terminal = statusEvent.Terminal,
            SanitizedError = statusEvent.SanitizedError,
            CurrentBuild = statusEvent.CurrentBuild is null
                ? null
                : StableDiffusionCppSourceBuildDescriptorHubMessage.FromContract(statusEvent.CurrentBuild)
        };
    }
}

internal sealed class StableDiffusionCppSourceBuildDescriptorHubMessage
{
    public required Guid BuildId { get; init; }
    public required string Backend { get; init; }
    public required string Source { get; init; }
    public required string Repository { get; init; }
    public required string RevisionMode { get; init; }
    public string? RequestedCommit { get; init; }
    public string? ResolvedCommit { get; init; }

    public static StableDiffusionCppSourceBuildDescriptorHubMessage FromContract(StableDiffusionCppSourceBuildDescriptor descriptor)
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
