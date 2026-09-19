namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;

using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>Projects the managed source-build contract types onto the wire DTOs, and back for the one request that carries choices.</summary>
internal static class WhisperCppSourceBuildMapper
{
    public static WhisperCppSourceBuildRequest ToContract(this StartWhisperCppSourceBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new WhisperCppSourceBuildRequest
        {
            Backend = request.Backend.ToContract(),
            Source = request.Source == TranscriptionSourceSelectionDto.Official
                ? WhisperCppSourceSelection.Official
                : WhisperCppSourceSelection.Custom,
            Repository = request.Repository,
            Commit = request.Commit,
            AcknowledgeCustomSourceRisk = request.AcknowledgeCustomSourceRisk
        };
    }

    public static WhisperBackend ToContract(this TranscriptionBackendDto backend)
    {
        return backend switch
        {
            TranscriptionBackendDto.Cpu => WhisperBackend.Cpu,
            TranscriptionBackendDto.Cuda => WhisperBackend.Cuda,
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown transcription source backend.")
        };
    }

    public static WhisperCppSourceBuildPrerequisitesResponse ToResponse(this WhisperCppSourceBuildPrerequisiteReport report,
        WhisperBackend backend)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new WhisperCppSourceBuildPrerequisitesResponse
        {
            Backend = backend.ToDto(),
            CanBuild = report.CanBuild,
            Items =
            [
                .. report.Items.Select(static item => new WhisperCppSourceBuildPrerequisiteItemResponse
                {
                    Key = item.Key,
                    Satisfied = item.Satisfied,
                    Detail = item.Detail
                })
            ]
        };
    }

    public static WhisperCppSourceBuildStatusResponse ToResponse(this WhisperCppSourceBuildStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new WhisperCppSourceBuildStatusResponse
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

    public static WhisperCppSourceBuildDescriptorResponse ToResponse(this WhisperCppSourceBuildDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new WhisperCppSourceBuildDescriptorResponse
        {
            BuildId = descriptor.BuildId,
            Backend = descriptor.Backend.ToDto(),
            Source = descriptor.Source == WhisperCppSourceSelection.Official
                ? TranscriptionSourceSelectionDto.Official
                : TranscriptionSourceSelectionDto.Custom,
            Repository = descriptor.Repository,
            RevisionMode = descriptor.RevisionMode switch
            {
                WhisperCppSourceRevisionMode.EnginePinned => TranscriptionSourceRevisionModeDto.EnginePinned,
                WhisperCppSourceRevisionMode.DefaultBranch => TranscriptionSourceRevisionModeDto.DefaultBranch,
                WhisperCppSourceRevisionMode.ExplicitCommit => TranscriptionSourceRevisionModeDto.ExplicitCommit,
                _ => throw new ArgumentOutOfRangeException(nameof(descriptor), descriptor.RevisionMode, "Unknown source revision mode.")
            },
            RequestedCommit = descriptor.RequestedCommit,
            ResolvedCommit = descriptor.ResolvedCommit
        };
    }
}
