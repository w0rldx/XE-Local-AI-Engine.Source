namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>Validation state of the managed stable-diffusion.cpp runtime record.</summary>
public enum StableDiffusionInstalledRuntimeValidity
{
    Active = 0,
    Invalid = 1
}

/// <summary>
///     Authoritative managed-runtime record. An <see cref="StableDiffusionInstalledRuntimeValidity.Invalid"/> record is a
///     fail-closed tombstone: desired backend/source selection persists after corruption so resolution cannot silently
///     replace the operator-selected runtime with a different prebuilt.
/// </summary>
public sealed record StableDiffusionInstalledRuntimeState(
    StableDiffusionInstalledRuntimeValidity Validity,
    SdGpuBackend DesiredBackend,
    string SourceRepository,
    string SourceCommit,
    StableDiffusionCppSourceSelection SourceSelection,
    StableDiffusionCppSourceRevisionMode SourceRevisionMode,
    string? SourceRequestedCommit,
    string? SourceBuildPath,
    string? ServerSha256,
    DateTimeOffset InstalledAtUtc,
    string? InvalidReason = null);

/// <summary>Secure, atomic persistence for the managed stable-diffusion.cpp runtime record.</summary>
public interface IStableDiffusionInstalledRuntimeStore
{
    Task<StableDiffusionInstalledRuntimeState?> ReadAsync(CancellationToken ct);
    Task WriteAsync(StableDiffusionInstalledRuntimeState state, CancellationToken ct);
    Task DeleteAsync(CancellationToken ct);
}
