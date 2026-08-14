namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>Exact, provider-resolved remote facts used by acquisition preflight and staging.</summary>
public sealed record ResolvedGgufDownload(
    string ModelBaseName,
    string CanonicalQuant,
    string RepoId,
    string ResolvedRevision,
    string SourceDisplayName,
    long SourceSizeBytes,
    string SourceSha256,
    GgufRole Role,
    ResolvedGgufProjectorDownload? Projector);

/// <summary>Exact optional projector companion resolved at the same source revision as the weights.</summary>
public sealed record ResolvedGgufProjectorDownload(
    string SourceDisplayName,
    long SourceSizeBytes,
    string SourceSha256);

/// <summary>Application-owned deterministic destination, revalidated by the provider.</summary>
public sealed record GgufDownloadDestination(
    string CanonicalModelName,
    string CanonicalQuant,
    string RelativeGgufPath,
    string RelativeSidecarPath,
    string? ProjectorRelativePath);

/// <summary>Prepared, non-visible download staged in operation-owned temporary files.</summary>
public sealed record PreparedGgufDownload(
    string OperationId,
    ResolvedGgufDownload Source,
    GgufDownloadDestination Destination,
    string TemporaryGgufPath,
    string TemporarySidecarPath,
    string? TemporaryProjectorPath,
    GgufModelRegistryEntry RegistryEntry,
    GgufAcquisitionMetadata Sidecar,
    string WeightMemberFingerprint,
    string? ProjectorMemberFingerprint,
    string ModelContentFingerprint);

/// <summary>Exact provider-owned artifacts created by a successful download commit.</summary>
public sealed record GgufDownloadCommitReceipt(
    GgufModelRegistryEntry RegistryEntry,
    string FinalGgufPath,
    string FinalSidecarPath,
    string? FinalProjectorPath,
    string WeightMemberFingerprint,
    string? ProjectorMemberFingerprint,
    string ModelContentFingerprint);

/// <summary>Staged Hugging Face GGUF filesystem/registry transaction.</summary>
public interface IGgufDownloadTransaction
{
    Task<ResolvedGgufDownload> ResolveAsync(GgufModelRequest request, CancellationToken cancellationToken);

    Task<PreparedGgufDownload> PrepareAsync(ResolvedGgufDownload source,
        GgufDownloadDestination destination,
        IProgress<PullProgress>? progress,
        CancellationToken cancellationToken);

    Task<GgufDownloadCommitReceipt> CommitAsync(PreparedGgufDownload preparedDownload, CancellationToken cancellationToken);

    Task RollbackCommittedAsync(GgufDownloadCommitReceipt commitReceipt, CancellationToken cancellationToken);

    Task DiscardPreparedAsync(PreparedGgufDownload preparedDownload, CancellationToken cancellationToken);
}
