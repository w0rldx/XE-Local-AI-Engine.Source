namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The authoritative record of a managed source-built runtime; <typeparamref name="TBackend" /> is the runtime's own
///     backend enum.
/// </summary>
/// <remarks>
///     An <see cref="ManagedRuntimeValidity.Invalid" /> record is a fail-closed tombstone, not an absent one: the
///     desired backend and source selection persist through corruption, so resolution can never silently replace the
///     operator-selected runtime with a prebuilt that contradicts it. Recovery is an explicit remove or rebuild.
/// </remarks>
public sealed record ManagedRuntimeState<TBackend>(
    ManagedRuntimeValidity Validity,
    TBackend DesiredBackend,
    string SourceRepository,
    string SourceCommit,
    ManagedSourceSelection SourceSelection,
    ManagedSourceRevisionMode SourceRevisionMode,
    string? SourceRequestedCommit,
    string? SourceBuildPath,
    string? ServerSha256,
    DateTimeOffset InstalledAtUtc,
    string? InvalidReason = null)
    where TBackend : struct, Enum;
