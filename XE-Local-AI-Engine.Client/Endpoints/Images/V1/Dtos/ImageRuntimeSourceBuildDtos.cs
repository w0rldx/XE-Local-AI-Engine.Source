namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using System.Text.Json.Serialization;

public enum StableDiffusionCppSourceBackendDto
{
    [JsonStringEnumMemberName("cpu")]
    Cpu = 0,

    [JsonStringEnumMemberName("vulkan")]
    Vulkan = 1,

    [JsonStringEnumMemberName("cuda")]
    Cuda = 2
}

public enum StableDiffusionCppSourceSelectionDto
{
    [JsonStringEnumMemberName("official")]
    Official = 0,

    [JsonStringEnumMemberName("custom")]
    Custom = 1
}

public enum StableDiffusionCppSourceRevisionModeDto
{
    [JsonStringEnumMemberName("enginePinned")]
    EnginePinned = 0,

    [JsonStringEnumMemberName("defaultBranch")]
    DefaultBranch = 1,

    [JsonStringEnumMemberName("explicitCommit")]
    ExplicitCommit = 2
}

public enum StableDiffusionInstalledRuntimeValidityDto
{
    [JsonStringEnumMemberName("active")]
    Active = 0,

    [JsonStringEnumMemberName("invalid")]
    Invalid = 1
}

/// <summary>Explicit action JSON body used by generated clients for otherwise body-less image-runtime POST actions.</summary>
public sealed class ImageRuntimeActionRequest
{
    /// <summary>
    ///     Optional transport placeholder. Generated clients send an explicit JSON object for these otherwise body-less
    ///     POST actions; the server does not require or interpret this value.
    /// </summary>
    public bool? Accepted { get; init; }
}

public sealed class StartStableDiffusionCppSourceBuildRequest
{
    public required StableDiffusionCppSourceBackendDto Backend { get; init; }
    public required StableDiffusionCppSourceSelectionDto Source { get; init; }
    public string? Repository { get; init; }
    public string? Commit { get; init; }
    public required bool AcknowledgeCustomSourceRisk { get; init; }
}

public sealed class GetStableDiffusionCppSourceBuildPrerequisitesRequest
{
    public required StableDiffusionCppSourceBackendDto Backend { get; init; }
}

public sealed class StableDiffusionCppSourceBuildPrerequisiteItemResponse
{
    public required string Key { get; init; }
    public required bool Satisfied { get; init; }
    public required string Detail { get; init; }
}

public sealed class StableDiffusionCppSourceBuildPrerequisitesResponse
{
    public required StableDiffusionCppSourceBackendDto Backend { get; init; }
    public required IReadOnlyList<StableDiffusionCppSourceBuildPrerequisiteItemResponse> Items { get; init; }
    public required bool CanBuild { get; init; }
}

public sealed class StableDiffusionCppSourceBuildDescriptorResponse
{
    public required Guid BuildId { get; init; }
    public required StableDiffusionCppSourceBackendDto Backend { get; init; }
    public required StableDiffusionCppSourceSelectionDto Source { get; init; }
    public required string Repository { get; init; }
    public required StableDiffusionCppSourceRevisionModeDto RevisionMode { get; init; }
    public string? RequestedCommit { get; init; }
    public string? ResolvedCommit { get; init; }
}

public sealed class StableDiffusionCppSourceBuildStatusResponse
{
    public required string Phase { get; init; }
    public required bool IsRunning { get; init; }
    public required bool Terminal { get; init; }
    public required long LogStartSequence { get; init; }
    public required IReadOnlyList<string> LogLines { get; init; }
    public string? SanitizedError { get; init; }
    public StableDiffusionCppSourceBuildDescriptorResponse? CurrentBuild { get; init; }
    public long? StartedAtUtc { get; init; }
    public long? CompletedAtUtc { get; init; }
}

public sealed class StartStableDiffusionCppSourceBuildResponse
{
    public required bool Started { get; init; }
    public required StableDiffusionCppSourceBuildStatusResponse Status { get; init; }
}

public sealed class ImageRuntimeActivityResponse
{
    public required int ActiveJobCount { get; init; }
    public required int SpawnReadinessCount { get; init; }
    public required int ResidentProcessCount { get; init; }
    public required bool MutationReserved { get; init; }
    public required bool EvictionReserved { get; init; }
    public required bool IsBusy { get; init; }
}

public sealed class StableDiffusionInstalledRuntimeResponse
{
    public required StableDiffusionInstalledRuntimeValidityDto Validity { get; init; }
    public required StableDiffusionCppSourceBackendDto DesiredBackend { get; init; }
    public required string SourceRepository { get; init; }
    public required string SourceCommit { get; init; }
    public required StableDiffusionCppSourceSelectionDto SourceSelection { get; init; }
    public required StableDiffusionCppSourceRevisionModeDto SourceRevisionMode { get; init; }
    public string? SourceRequestedCommit { get; init; }
    public required long InstalledAtUtc { get; init; }
    public string? InvalidReason { get; init; }
}

public sealed class ImageRuntimeStatusResponse
{
    public StableDiffusionInstalledRuntimeResponse? ManagedRuntime { get; init; }
    public required ImageRuntimeActivityResponse Activity { get; init; }
}

public sealed class ImageRuntimeBlockedResponse
{
    public required string Reason { get; init; }
    public required string Message { get; init; }
    public required ImageRuntimeActivityResponse Activity { get; init; }
}
