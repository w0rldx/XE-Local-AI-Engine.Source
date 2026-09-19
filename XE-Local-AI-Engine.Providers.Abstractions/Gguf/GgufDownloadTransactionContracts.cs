namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>Exact, provider-resolved remote facts used by acquisition preflight and staging.</summary>
public sealed record ResolvedGgufDownload
{
    public required string ModelBaseName { get; init; }

    public required string CanonicalQuant { get; init; }

    public required string RepoId { get; init; }

    public required string ResolvedRevision { get; init; }

    public required string SourceDisplayName { get; init; }

    public required long SourceSizeBytes { get; init; }

    public required string SourceSha256 { get; init; }

    public required GgufRole Role { get; init; }

    public required ResolvedGgufProjectorDownload? Projector { get; init; }
}

/// <summary>Exact optional projector companion resolved at the same source revision as the weights.</summary>
public sealed record ResolvedGgufProjectorDownload
{
    public required string SourceDisplayName { get; init; }

    public required long SourceSizeBytes { get; init; }

    public required string SourceSha256 { get; init; }
}

/// <summary>Application-owned deterministic destination, revalidated by the provider.</summary>
public sealed class GgufDownloadDestination
{
    public required string CanonicalModelName { get; init; }

    public required string CanonicalQuant { get; init; }

    public required string RelativeGgufPath { get; init; }

    public required string RelativeSidecarPath { get; init; }

    public required string? ProjectorRelativePath { get; init; }
}

/// <summary>Prepared, non-visible download staged in operation-owned temporary files.</summary>
public sealed class PreparedGgufDownload
{
    public required string OperationId { get; init; }

    public required ResolvedGgufDownload Source { get; init; }

    public required GgufDownloadDestination Destination { get; init; }

    public required string TemporaryGgufPath { get; init; }

    public required string TemporarySidecarPath { get; init; }

    public required string? TemporaryProjectorPath { get; init; }

    public required GgufModelRegistryEntry RegistryEntry { get; init; }

    public required GgufAcquisitionMetadata Sidecar { get; init; }

    public required string WeightMemberFingerprint { get; init; }

    public required string? ProjectorMemberFingerprint { get; init; }

    public required string ModelContentFingerprint { get; init; }
}

/// <summary>Exact provider-owned artifacts created by a successful download commit.</summary>
public sealed class GgufDownloadCommitReceipt
{
    public required GgufModelRegistryEntry RegistryEntry { get; init; }

    public required string FinalGgufPath { get; init; }

    public required string FinalSidecarPath { get; init; }

    public required string? FinalProjectorPath { get; init; }

    public required string WeightMemberFingerprint { get; init; }

    public required string? ProjectorMemberFingerprint { get; init; }

    public required string ModelContentFingerprint { get; init; }

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
