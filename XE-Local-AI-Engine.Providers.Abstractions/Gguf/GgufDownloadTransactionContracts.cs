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
    string ModelContentFingerprint)
{
    /// <summary>Whether this operation created the final weight path.</summary>
    public bool OwnsFinalGguf { get; init; } = true;

    /// <summary>Whether this operation created the final sidecar path.</summary>
    public bool OwnsFinalSidecar { get; init; } = true;

    /// <summary>Whether this operation created the optional final projector path.</summary>
    public bool OwnsFinalProjector { get; init; } = true;
}

/// <summary>A failed commit that created one or more final artifacts which still require compensation.</summary>
public sealed class GgufDownloadCommitException : Exception
{
    /// <summary>Creates a partial-commit failure with the exact ownership receipt required for retryable rollback.</summary>
    public GgufDownloadCommitException(GgufDownloadCommitReceipt commitReceipt, string sanitizedMessage, Exception innerException)
        : base(sanitizedMessage, innerException)
    {
        CommitReceipt = commitReceipt;
    }

    /// <summary>Exact final paths created before the commit failed.</summary>
    public GgufDownloadCommitReceipt CommitReceipt { get; }
}

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
