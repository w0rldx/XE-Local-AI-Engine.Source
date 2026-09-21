namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>Validation state of the managed whisper.cpp runtime record.</summary>
public enum WhisperInstalledRuntimeValidity
{
    Active = 0,
    Invalid = 1
}

/// <summary>
///     The authoritative managed-runtime record.
/// </summary>
/// <remarks>
///     An <see cref="WhisperInstalledRuntimeValidity.Invalid" /> record is a fail-closed tombstone, not an absent one:
///     the desired backend and source selection persist through corruption, so resolution can never silently replace
///     the operator-selected runtime with a prebuilt that contradicts it. Recovery is an explicit remove or rebuild.
/// </remarks>
public sealed record WhisperInstalledRuntimeState(
    WhisperInstalledRuntimeValidity Validity,
    WhisperBackend DesiredBackend,
    string SourceRepository,
    string SourceCommit,
    WhisperCppSourceSelection SourceSelection,
    WhisperCppSourceRevisionMode SourceRevisionMode,
    string? SourceRequestedCommit,
    string? SourceBuildPath,
    string? ServerSha256,
    DateTimeOffset InstalledAtUtc,
    string? InvalidReason = null);

/// <summary>Secure, atomic persistence for the managed whisper.cpp runtime record.</summary>
public interface IWhisperInstalledRuntimeStore
{
    Task<WhisperInstalledRuntimeState?> ReadAsync(CancellationToken ct);

    Task WriteAsync(WhisperInstalledRuntimeState state, CancellationToken ct);

    Task DeleteAsync(CancellationToken ct);
}
