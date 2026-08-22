namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Providers.LlamaServer;

public enum LlamaCppRuntimeAdministrationFailure
{
    None = 0,
    InvalidRequest = 1,
    Busy = 2,
    RuntimeFailure = 3
}

public sealed record LlamaCppRuntimeStatus(
    LlamaCppInstalledRuntimeView? Installed,
    string RecommendedTag,
    string? UpstreamLatestTag,
    bool UpdateAvailable,
    bool IsOffline,
    int RunningProcessCount);

public sealed record LlamaCppRuntimeMutationResult(
    bool Succeeded,
    LlamaCppRuntimeBinaryView? Binary,
    string? RecommendedTag,
    LlamaCppRuntimeAdministrationFailure Failure,
    string? DisplayMessage,
    int RunningProcessCount = 0)
{
    public static LlamaCppRuntimeMutationResult Success(LlamaCppRuntimeBinaryView binary, string recommendedTag) =>
        new(true, binary, recommendedTag, LlamaCppRuntimeAdministrationFailure.None, DisplayMessage: null);

    public static LlamaCppRuntimeMutationResult Rejected(LlamaCppRuntimeAdministrationFailure failure,
        string message,
        int runningProcessCount = 0) =>
        new(false, Binary: null, RecommendedTag: null, failure, message, runningProcessCount);
}

public sealed record LlamaCppRuntimeAcquisitionStartResult(
    bool Accepted,
    string? Variant,
    LlamaCppRuntimeAdministrationFailure Failure,
    string? DisplayMessage,
    int RunningProcessCount = 0);

public sealed record LlamaCppRuntimeBinaryView(
    string Version,
    string Variant,
    bool IsPinnedFallback);

public sealed record LlamaCppInstalledRuntimeView(
    string Tag,
    string Asset,
    string Variant,
    long InstalledAtUnixTimeMilliseconds,
    bool IsSourceBuild,
    string? SourceRepository,
    string? SourceCommit,
    int? SourceRevisionMode,
    string? SourceRequestedCommit,
    int? SourceSelection);

public sealed record LlamaCppRuntimeAcquisitionStatus(
    long Sequence,
    string Phase,
    string? Variant,
    string? Tag,
    long? CompletedBytes,
    long? TotalBytes,
    int StepIndex,
    int StepCount,
    string? SanitizedError);

/// <summary>Transport-neutral application boundary for managed llama.cpp runtime administration.</summary>
public interface ILlamaCppRuntimeAdministrationService
{
    Task<LlamaCppRuntimeStatus> GetStatusAsync(bool refresh = false, CancellationToken cancellationToken = default);

    LlamaCppRuntimeAcquisitionStatus GetAcquisitionStatus();

    Task<LlamaCppRuntimeMutationResult> EnsureAsync(GpuVariant variant, CancellationToken cancellationToken = default);

    Task<LlamaCppRuntimeMutationResult> InstallAsync(string tag,
        GpuVariant? variant = null,
        CancellationToken cancellationToken = default);

    Task<LlamaCppRuntimeAcquisitionStartResult> StartAcquisitionAsync(GpuVariant? variant = null,
        CancellationToken cancellationToken = default);
}
