namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>Operator-selected local source, scoped to the in-process provider call.</summary>
public sealed record GgufImportSource(string AbsolutePath);

/// <summary>Application-resolved, provider-revalidated import destination.</summary>
public sealed record GgufImportDestination(
    string CanonicalModelName,
    string CanonicalQuant,
    string RelativeGgufPath,
    string RelativeSidecarPath,
    LocalModelOrigin Origin,
    string? ProjectorRelativePath = null);

/// <summary>Supported V1 imported workload.</summary>
public enum GgufImportWorkload
{
    /// <summary>Causal/chat generation model.</summary>
    CausalChat = 0
}

/// <summary>Stable strict-inspection rejection codes.</summary>
public enum GgufImportRejectionCode
{
    InvalidSource,
    DestinationConflict,
    InvalidGguf,
    UnsupportedVersion,
    SplitModel,
    UnsupportedModelType,
    UnsupportedArchitecture,
    QuantizationRequired,
    UnsupportedQuantization
}

/// <summary>Safe strict-inspection result; never carries the operator path.</summary>
public sealed record GgufImportInspection(
    long SizeBytes,
    uint? GgufVersion,
    string? Architecture,
    GgufImportWorkload? Workload,
    string? DetectedQuantization,
    string SourceDisplayName,
    IReadOnlyList<GgufImportRejectionCode> Rejections,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    ///     Opaque identity of the exact validated source file. Callers may compare it across preview/start without
    ///     learning the source path or platform file identifier.
    /// </summary>
    public string SourceIdentityToken { get; init; } = string.Empty;

    /// <summary>True when no locked rejection was found.</summary>
    public bool IsAccepted => Rejections.Count == 0;
}

/// <summary>Strict local GGUF preview/execution inspection seam.</summary>
public interface IGgufImportInspector
{
    Task<GgufImportInspection> InspectAsync(GgufImportSource source, CancellationToken cancellationToken);
}

/// <summary>Copy progress for an import preparation.</summary>
public sealed record GgufImportProgress(long CompletedBytes, long TotalBytes);

/// <summary>Prepared, non-visible import staged in operation-owned temporary files.</summary>
public sealed record PreparedGgufImport(
    string OperationId,
    GgufImportDestination Destination,
    string TemporaryGgufPath,
    string TemporarySidecarPath,
    GgufModelRegistryEntry RegistryEntry,
    GgufAcquisitionMetadata Sidecar,
    string WeightMemberFingerprint,
    string ModelContentFingerprint);

/// <summary>Exact provider-owned artifacts created by a successful import commit.</summary>
public sealed record GgufImportCommitReceipt(
    GgufModelRegistryEntry RegistryEntry,
    string FinalGgufPath,
    string FinalSidecarPath,
    string WeightMemberFingerprint,
    string ModelContentFingerprint);

/// <summary>Staged local GGUF filesystem/registry transaction.</summary>
public interface IGgufModelImporter
{
    Task<PreparedGgufImport> PrepareAsync(GgufImportSource source,
        GgufImportDestination destination,
        IProgress<GgufImportProgress>? progress,
        CancellationToken cancellationToken);

    Task<GgufImportCommitReceipt> CommitAsync(PreparedGgufImport preparedImport, CancellationToken cancellationToken);

    Task RollbackCommittedAsync(GgufImportCommitReceipt commitReceipt, CancellationToken cancellationToken);

    Task DiscardPreparedAsync(PreparedGgufImport preparedImport, CancellationToken cancellationToken);
}

/// <summary>Sanitized provider failure for strict local import operations.</summary>
public sealed class GgufImportException : Exception
{
    public GgufImportException(GgufImportRejectionCode reason, string sanitizedMessage)
        : base(sanitizedMessage)
    {
        Reason = reason;
    }

    public GgufImportException(GgufImportRejectionCode reason, string sanitizedMessage, Exception innerException)
        : base(sanitizedMessage, innerException)
    {
        Reason = reason;
    }

    public GgufImportRejectionCode Reason { get; }
}
