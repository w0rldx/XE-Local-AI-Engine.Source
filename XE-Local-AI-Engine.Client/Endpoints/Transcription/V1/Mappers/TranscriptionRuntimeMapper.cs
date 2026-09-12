namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>Projects the transcription runtime's contract types onto the wire DTOs.</summary>
internal static class TranscriptionRuntimeMapper
{
    public static TranscriptionRuntimeStatusResponse ToResponse(this TranscriptionRuntimeView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new TranscriptionRuntimeStatusResponse
        {
            Enabled = view.Enabled,
            State = view.Runtime.State.ToDto(),
            Backend = view.Runtime.Backend?.ToDto(),
            BinarySource = view.Runtime.BinarySource?.ToDto(),
            BinaryVersion = view.Runtime.BinaryVersion,
            LoadedModelId = view.Runtime.LoadedModelId,
            SelectedModelId = view.SelectedModelId,
            RecommendedModelId = view.RecommendedModelId,
            SupportsTranscode = view.Runtime.SupportsTranscode,
            IdleTimeoutMinutes = view.IdleTimeoutMinutes,
            VadInstalled = view.VadInstalled,
            ManagedRuntime = view.ManagedRuntime?.ToResponse(),
            Activity = view.Activity.ToResponse()
        };
    }

    public static TranscriptionRuntimeActivityResponse ToResponse(this WhisperRuntimeActivitySnapshot activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return new TranscriptionRuntimeActivityResponse
        {
            ActiveTranscriptionCount = activity.ActiveTranscriptionCount,
            SpawnReadinessCount = activity.SpawnReadinessCount,
            ResidentProcessCount = activity.ResidentProcessCount,
            MutationReserved = activity.MutationReserved,
            EvictionReserved = activity.EvictionReserved,
            IsBusy = activity.IsBusy
        };
    }

    public static WhisperInstalledRuntimeResponse ToResponse(this WhisperInstalledRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new WhisperInstalledRuntimeResponse
        {
            Validity = state.Validity == WhisperInstalledRuntimeValidity.Active
                ? TranscriptionInstalledRuntimeValidityDto.Active
                : TranscriptionInstalledRuntimeValidityDto.Invalid,
            DesiredBackend = state.DesiredBackend.ToDto(),
            SourceRepository = state.SourceRepository,
            SourceCommit = state.SourceCommit,
            SourceSelection = state.SourceSelection == WhisperCppSourceSelection.Official
                ? TranscriptionSourceSelectionDto.Official
                : TranscriptionSourceSelectionDto.Custom,
            SourceRevisionMode = state.SourceRevisionMode switch
            {
                WhisperCppSourceRevisionMode.EnginePinned => TranscriptionSourceRevisionModeDto.EnginePinned,
                WhisperCppSourceRevisionMode.DefaultBranch => TranscriptionSourceRevisionModeDto.DefaultBranch,
                WhisperCppSourceRevisionMode.ExplicitCommit => TranscriptionSourceRevisionModeDto.ExplicitCommit,
                _ => throw new ArgumentOutOfRangeException(nameof(state), state.SourceRevisionMode, "Unknown source revision mode.")
            },
            SourceRequestedCommit = state.SourceRequestedCommit,
            InstalledAtUtc = state.InstalledAtUtc.ToUnixTimeMilliseconds(),
            InvalidReason = state.InvalidReason
        };
    }

    public static TranscriptionModelRecommendationResponse ToRecommendationResponse(this WhisperModelEntry entry, WhisperBackend backend)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new TranscriptionModelRecommendationResponse
        {
            RecommendedModelId = entry.Id,
            Tier = entry.Tier.ToString(),
            ApproximateVramBytes = entry.ApproximateVramBytes,
            ApproximateRamBytes = entry.ApproximateRamBytes,
            Backend = backend.ToDto()
        };
    }

    public static TranscriptionBackendDto ToDto(this WhisperBackend backend)
    {
        return backend switch
        {
            WhisperBackend.Cpu => TranscriptionBackendDto.Cpu,
            WhisperBackend.Cuda => TranscriptionBackendDto.Cuda,
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown transcription backend.")
        };
    }

    private static TranscriptionRuntimeStateDto ToDto(this WhisperRuntimeState state)
    {
        return state switch
        {
            WhisperRuntimeState.Stopped => TranscriptionRuntimeStateDto.Stopped,
            WhisperRuntimeState.Starting => TranscriptionRuntimeStateDto.Starting,
            WhisperRuntimeState.Ready => TranscriptionRuntimeStateDto.Ready,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown transcription runtime state.")
        };
    }

    private static TranscriptionBinarySourceDto ToDto(this WhisperBinarySource source)
    {
        return source switch
        {
            WhisperBinarySource.Pinned => TranscriptionBinarySourceDto.Pinned,
            WhisperBinarySource.Managed => TranscriptionBinarySourceDto.Managed,
            WhisperBinarySource.BringYourOwn => TranscriptionBinarySourceDto.BringYourOwn,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown transcription binary source.")
        };
    }
}
