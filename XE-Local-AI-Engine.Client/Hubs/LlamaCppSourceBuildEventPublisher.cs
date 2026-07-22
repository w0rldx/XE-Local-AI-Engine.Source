namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

internal sealed class LlamaCppSourceBuildEventPublisher(
    IHubContext<LlamaCppSourceBuildHub> sourceHubContext,
    IHubContext<CudaBuildHub> cudaHubContext) : ILlamaCppSourceBuildEventPublisher
{
    public async Task PublishStatusAsync(LlamaCppSourceBuildStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statusEvent);
        await sourceHubContext.Clients.All.SendAsync(LlamaCppSourceBuildHubEvents.StatusChanged,
            LlamaCppSourceBuildStatusHubMessage.FromContract(statusEvent), cancellationToken).ConfigureAwait(false);

        if (statusEvent.CurrentBuild.IsLegacyPinnedCuda())
        {
            await cudaHubContext.Clients.All.SendAsync(CudaBuildHubEvents.StatusChanged,
                new CudaBuildStatusHubEvent(statusEvent.Phase, statusEvent.AppendedLogLines, statusEvent.Terminal, statusEvent.SanitizedError),
                cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
///     Stable SignalR wire shape. Provider contracts intentionally remain transport-agnostic; this projection keeps
///     their CLR enums from leaking as numeric or Pascal-cased values and can absorb future descriptor fields without
///     changing the provider event contract.
/// </summary>
internal sealed class LlamaCppSourceBuildStatusHubMessage
{
    public required string Phase { get; init; }
    public required IReadOnlyList<string> AppendedLogLines { get; init; }
    public required bool Terminal { get; init; }
    public string? SanitizedError { get; init; }
    public LlamaCppSourceBuildDescriptorHubMessage? CurrentBuild { get; init; }

    public static LlamaCppSourceBuildStatusHubMessage FromContract(LlamaCppSourceBuildStatusHubEvent statusEvent)
    {
        return new LlamaCppSourceBuildStatusHubMessage
        {
            Phase = statusEvent.Phase,
            AppendedLogLines = statusEvent.AppendedLogLines,
            Terminal = statusEvent.Terminal,
            SanitizedError = statusEvent.SanitizedError,
            CurrentBuild = statusEvent.CurrentBuild is null
                ? null
                : LlamaCppSourceBuildDescriptorHubMessage.FromContract(statusEvent.CurrentBuild)
        };
    }
}

internal sealed class LlamaCppSourceBuildDescriptorHubMessage
{
    public required Guid BuildId { get; init; }
    public required string Backend { get; init; }
    public required string Source { get; init; }
    public required string Repository { get; init; }
    public required string RevisionMode { get; init; }
    public string? RequestedCommit { get; init; }
    public string? ResolvedCommit { get; init; }

    public static LlamaCppSourceBuildDescriptorHubMessage FromContract(LlamaCppSourceBuildDescriptor descriptor)
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
