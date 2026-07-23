namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using System.Text.Json.Serialization;

public enum LlamaCppSourceBackendDto
{
    [JsonStringEnumMemberName("cpu")]
    Cpu = 0,
    [JsonStringEnumMemberName("vulkan")]
    Vulkan = 1,
    [JsonStringEnumMemberName("cuda")]
    Cuda = 2
}

public enum LlamaCppSourceSelectionDto
{
    [JsonStringEnumMemberName("official")]
    Official = 0,
    [JsonStringEnumMemberName("custom")]
    Custom = 1
}

public enum LlamaCppSourceRevisionModeDto
{
    [JsonStringEnumMemberName("enginePinned")]
    EnginePinned = 0,
    [JsonStringEnumMemberName("defaultBranch")]
    DefaultBranch = 1,
    [JsonStringEnumMemberName("explicitCommit")]
    ExplicitCommit = 2
}

public sealed class StartLlamaCppSourceBuildRequest
{
    public required LlamaCppSourceBackendDto Backend { get; init; }
    public required LlamaCppSourceSelectionDto Source { get; init; }
    public string? Repository { get; init; }
    public string? Commit { get; init; }
    public required bool AcknowledgeCustomSourceRisk { get; init; }
}

public sealed class GetLlamaCppSourceBuildPrerequisitesRequest
{
    public required LlamaCppSourceBackendDto Backend { get; init; }
}

public sealed class LlamaCppSourceBuildPrerequisiteItemResponse
{
    public required string Key { get; init; }
    public required bool Satisfied { get; init; }
    public required string Detail { get; init; }
}

public sealed class LlamaCppSourceBuildPrerequisitesResponse
{
    public required LlamaCppSourceBackendDto Backend { get; init; }
    public required IReadOnlyList<LlamaCppSourceBuildPrerequisiteItemResponse> Items { get; init; }
    public required bool CanBuild { get; init; }
}

public sealed class LlamaCppSourceBuildDescriptorResponse
{
    public required Guid BuildId { get; init; }
    public required LlamaCppSourceBackendDto Backend { get; init; }
    public required LlamaCppSourceSelectionDto Source { get; init; }
    public required string Repository { get; init; }
    public required LlamaCppSourceRevisionModeDto RevisionMode { get; init; }
    public string? RequestedCommit { get; init; }
    public string? ResolvedCommit { get; init; }
}

public sealed class LlamaCppSourceBuildStatusResponse
{
    public required string Phase { get; init; }
    public required bool IsRunning { get; init; }
    public required bool Terminal { get; init; }
    public required long LogStartSequence { get; init; }
    public required IReadOnlyList<string> LogLines { get; init; }
    public string? SanitizedError { get; init; }
    public LlamaCppSourceBuildDescriptorResponse? CurrentBuild { get; init; }
    public long? StartedAtUtc { get; init; }
    public long? CompletedAtUtc { get; init; }
}

public sealed class StartLlamaCppSourceBuildResponse
{
    public required bool Started { get; init; }
    public required LlamaCppSourceBuildStatusResponse Status { get; init; }
}

public sealed class LlamaCppSourceBuildBlockedResponse
{
    public required string Reason { get; init; }
    public required string Message { get; init; }
    public int? RunningProcessCount { get; init; }
}
