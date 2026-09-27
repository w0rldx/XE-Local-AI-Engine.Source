namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

internal sealed class LlamaCppSourceBuildEventPublisher : ILlamaCppSourceBuildEventPublisher
{
    private readonly IHubContext<LlamaCppSourceBuildHub> _sourceHubContext;

    public LlamaCppSourceBuildEventPublisher(IHubContext<LlamaCppSourceBuildHub> sourceHubContext)
    {
        _sourceHubContext = sourceHubContext;
    }

    public Task PublishStatusAsync(LlamaCppSourceBuildStatusEvent statusEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statusEvent);
        return _sourceHubContext.Clients.All.SendAsync(LlamaCppSourceBuildHubEvents.StatusChanged,
            ToHubMessage(statusEvent), cancellationToken);
    }

    private static LlamaCppSourceBuildStatusHubMessage ToHubMessage(LlamaCppSourceBuildStatusEvent statusEvent)
    {
        return new LlamaCppSourceBuildStatusHubMessage
        {
            Phase = statusEvent.Phase,
            AppendedLogLines = statusEvent.AppendedLogLines,
            AppendedLogStartSequence = statusEvent.AppendedLogStartSequence,
            Terminal = statusEvent.Terminal,
            SanitizedError = statusEvent.SanitizedError,
            CurrentBuild = statusEvent.CurrentBuild is null
                ? null
                : ToDescriptorHubMessage(statusEvent.CurrentBuild)
        };
    }

    private static LlamaCppSourceBuildDescriptorHubMessage ToDescriptorHubMessage(LlamaCppSourceBuildDescriptor descriptor)
    {
        return new LlamaCppSourceBuildDescriptorHubMessage
        {
            BuildId = descriptor.BuildId,
            Backend = descriptor.Variant switch
            {
                GpuVariant.Cpu => "cpu",
                GpuVariant.Vulkan => "vulkan",
                GpuVariant.Cuda => "cuda",
                _ => throw new ArgumentOutOfRangeException(nameof(descriptor), descriptor.Variant, "Unknown source-build variant.")
            },
            Source = descriptor.Source switch
            {
                LlamaCppSourceSelection.Official => "official",
                LlamaCppSourceSelection.Custom => "custom",
                _ => throw new ArgumentOutOfRangeException(nameof(descriptor), descriptor.Source, "Unknown source selection.")
            },
            Repository = descriptor.Repository,
            RevisionMode = descriptor.RevisionMode switch
            {
                LlamaCppSourceRevisionMode.EnginePinned => "enginePinned",
                LlamaCppSourceRevisionMode.DefaultBranch => "defaultBranch",
                LlamaCppSourceRevisionMode.ExplicitCommit => "explicitCommit",
                _ => throw new ArgumentOutOfRangeException(nameof(descriptor), descriptor.RevisionMode, "Unknown source revision mode.")
            },
            RequestedCommit = descriptor.RequestedCommit,
            ResolvedCommit = descriptor.ResolvedCommit
        };
    }
}
