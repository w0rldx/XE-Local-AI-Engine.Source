namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Providers.LlamaServer;

public enum LlamaCppRuntimeAdministrationFailure
{
    None = 0,
    InvalidRequest = 1,
    Busy = 2,
    RuntimeFailure = 3
}

public sealed class LlamaCppRuntimeStatus
{
    public required LlamaCppInstalledRuntimeView? Installed { get; init; }

    public required string RecommendedTag { get; init; }

    public required string? UpstreamLatestTag { get; init; }

    public required bool UpdateAvailable { get; init; }

    public required bool IsOffline { get; init; }

    public required int RunningProcessCount { get; init; }

    public required DateTimeOffset? CheckedAtUtc { get; init; }

    /// <summary>The variant a bring-your-own <c>llama-server</c> override serves with; <see langword="null" /> when none is set.</summary>
    public GpuVariant? OverrideVariant { get; init; }

    /// <summary>The variant the next spawn selects: the override, else an operator pin or source build, else the vendor rule.</summary>
    public required GpuVariant SelectedVariant { get; init; }
}

public sealed class LlamaCppRuntimeMutationResult
{
    public required bool Succeeded { get; init; }

    public required LlamaCppRuntimeBinaryView? Binary { get; init; }

    public required string? RecommendedTag { get; init; }

    public required LlamaCppRuntimeAdministrationFailure Failure { get; init; }

    public required string? DisplayMessage { get; init; }

    public int RunningProcessCount { get; init; }

    public static LlamaCppRuntimeMutationResult Success(LlamaCppRuntimeBinaryView binary, string recommendedTag) =>
        new()
        {
            Succeeded = true,
            Binary = binary,
            RecommendedTag = recommendedTag,
            Failure = LlamaCppRuntimeAdministrationFailure.None,
            DisplayMessage = null
        };

    public static LlamaCppRuntimeMutationResult Rejected(LlamaCppRuntimeAdministrationFailure failure,
        string message,
        int runningProcessCount = 0) =>
        new()
        {
            Succeeded = false,
            Binary = null,
            RecommendedTag = null,
            Failure = failure,
            DisplayMessage = message,
            RunningProcessCount = runningProcessCount
        };
}

public sealed class LlamaCppRuntimeAcquisitionStartResult
{
    public required bool Accepted { get; init; }

    public required string? Variant { get; init; }

    public required LlamaCppRuntimeAdministrationFailure Failure { get; init; }

    public required string? DisplayMessage { get; init; }

    public int RunningProcessCount { get; init; }
}

public sealed class LlamaCppRuntimeBinaryView
{
    public required string Version { get; init; }

    public required string Variant { get; init; }

    public required bool IsPinnedFallback { get; init; }
}

public sealed class LlamaCppInstalledRuntimeView
{
    public required string Tag { get; init; }

    public required string Asset { get; init; }

    public required string Variant { get; init; }

    public required long InstalledAtUnixTimeMilliseconds { get; init; }

    public required bool IsSourceBuild { get; init; }

    public required string? SourceRepository { get; init; }

    public required string? SourceCommit { get; init; }

    public required int? SourceRevisionMode { get; init; }

    public required string? SourceRequestedCommit { get; init; }

    public required int? SourceSelection { get; init; }

    /// <summary>True while an operator pin is in effect (recorded, or held by this process).</summary>
    public bool OperatorSelected { get; init; }
}

public sealed class LlamaCppRuntimeAcquisitionStatus
{
    public required long Sequence { get; init; }

    public required string Phase { get; init; }

    public required string? Variant { get; init; }

    public required string? Tag { get; init; }

    public required long? CompletedBytes { get; init; }

    public required long? TotalBytes { get; init; }

    public required int StepIndex { get; init; }

    public required int StepCount { get; init; }

    public required string? SanitizedError { get; init; }
}

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

    /// <summary>
    ///     Drops an operator variant pin so the next spawn follows the vendor rule again; a recorded source build is left
    ///     as it is. Returns the refusal message while a bring-your-own override is active, otherwise null.
    /// </summary>
    Task<string?> UseAutomaticVariantAsync(CancellationToken cancellationToken = default);
}
