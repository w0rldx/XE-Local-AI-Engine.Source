namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;

using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

internal static class ImageRuntimeSourceBuildMapper
{
    public static StableDiffusionCppSourceBuildRequest ToContract(this StartStableDiffusionCppSourceBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new StableDiffusionCppSourceBuildRequest(request.Backend.ToContract(),
            (StableDiffusionCppSourceSelection)(int)request.Source,
            request.Repository,
            request.Commit,
            request.AcknowledgeCustomSourceRisk);
    }

    public static SdGpuBackend ToContract(this StableDiffusionCppSourceBackendDto backend)
    {
        return backend switch
        {
            StableDiffusionCppSourceBackendDto.Cpu => SdGpuBackend.Cpu,
            StableDiffusionCppSourceBackendDto.Vulkan => SdGpuBackend.Vulkan,
            StableDiffusionCppSourceBackendDto.Cuda => SdGpuBackend.Cuda,
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown stable-diffusion.cpp source backend.")
        };
    }

    public static StableDiffusionCppSourceBuildPrerequisitesResponse ToResponse(
        this StableDiffusionCppSourceBuildPrerequisiteReport report,
        SdGpuBackend backend)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new StableDiffusionCppSourceBuildPrerequisitesResponse
        {
            Backend = backend.ToDto(),
            CanBuild = report.CanBuild,
            Items =
            [
                .. report.Items.Select(static item => new StableDiffusionCppSourceBuildPrerequisiteItemResponse
                {
                    Key = item.Key,
                    Satisfied = item.Satisfied,
                    Detail = item.Detail
                })
            ]
        };
    }

    public static StableDiffusionCppSourceBuildStatusResponse ToResponse(this StableDiffusionCppSourceBuildStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new StableDiffusionCppSourceBuildStatusResponse
        {
            Phase = status.Phase.ToWireString(),
            IsRunning = status.IsRunning,
            Terminal = status.Terminal,
            LogStartSequence = status.LogStartSequence,
            LogLines = status.LogLines,
            SanitizedError = status.SanitizedError,
            CurrentBuild = status.CurrentBuild?.ToResponse(),
            StartedAtUtc = status.StartedAtUtc?.ToUnixTimeMilliseconds(),
            CompletedAtUtc = status.CompletedAtUtc?.ToUnixTimeMilliseconds()
        };
    }

    public static ImageRuntimeActivityResponse ToResponse(this ImageRuntimeActivitySnapshot activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return new ImageRuntimeActivityResponse
        {
            ActiveJobCount = activity.ActiveJobCount,
            SpawnReadinessCount = activity.SpawnReadinessCount,
            ResidentProcessCount = activity.ResidentProcessCount,
            MutationReserved = activity.MutationReserved,
            EvictionReserved = activity.EvictionReserved,
            IsBusy = activity.IsBusy
        };
    }

    public static StableDiffusionInstalledRuntimeResponse ToResponse(this StableDiffusionInstalledRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new StableDiffusionInstalledRuntimeResponse
        {
            Validity = (StableDiffusionInstalledRuntimeValidityDto)(int)state.Validity,
            DesiredBackend = state.DesiredBackend.ToDto(),
            SourceRepository = state.SourceRepository,
            SourceCommit = state.SourceCommit,
            SourceSelection = (StableDiffusionCppSourceSelectionDto)(int)state.SourceSelection,
            SourceRevisionMode = (StableDiffusionCppSourceRevisionModeDto)(int)state.SourceRevisionMode,
            SourceRequestedCommit = state.SourceRequestedCommit,
            InstalledAtUtc = state.InstalledAtUtc.ToUnixTimeMilliseconds(),
            InvalidReason = state.InvalidReason
        };
    }

    public static StableDiffusionCppSourceBuildDescriptorResponse ToResponse(this StableDiffusionCppSourceBuildDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new StableDiffusionCppSourceBuildDescriptorResponse
        {
            BuildId = descriptor.BuildId,
            Backend = descriptor.Backend.ToDto(),
            Source = (StableDiffusionCppSourceSelectionDto)(int)descriptor.Source,
            Repository = descriptor.Repository,
            RevisionMode = (StableDiffusionCppSourceRevisionModeDto)(int)descriptor.RevisionMode,
            RequestedCommit = descriptor.RequestedCommit,
            ResolvedCommit = descriptor.ResolvedCommit
        };
    }

    private static StableDiffusionCppSourceBackendDto ToDto(this SdGpuBackend backend)
    {
        return backend switch
        {
            SdGpuBackend.Cpu => StableDiffusionCppSourceBackendDto.Cpu,
            SdGpuBackend.Vulkan => StableDiffusionCppSourceBackendDto.Vulkan,
            SdGpuBackend.Cuda => StableDiffusionCppSourceBackendDto.Cuda,
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown stable-diffusion.cpp source backend.")
        };
    }
}
