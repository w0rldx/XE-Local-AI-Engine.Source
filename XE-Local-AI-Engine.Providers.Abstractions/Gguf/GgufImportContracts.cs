namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>Operator-selected local source, scoped to the in-process provider call.</summary>
public sealed record GgufImportSource(string AbsolutePath);

/// <summary>
///     What a locally trained artifact was derived from, carried onto its registry entry and sidecar so a promoted
///     model can always name the checkpoint and dataset behind it.
/// </summary>
/// <param name="DerivedFromRepoId">Base checkpoint repository the run trained on.</param>
/// <param name="DerivedFromRevision">Resolved revision of <paramref name="DerivedFromRepoId" />.</param>
/// <param name="DerivedFromContentFingerprint">Frozen dataset content fingerprint the run consumed, when recorded.</param>
/// <param name="BaseModelName">
///     Set ONLY for a LoRA-adapter promotion: the installed model the adapter is applied on top of. Its presence is
///     what makes the destination an adapter rather than a standalone (merged) model.
/// </param>
public sealed record TrainedModelLineage(
    string? DerivedFromRepoId,
    string? DerivedFromRevision,
    string? DerivedFromContentFingerprint,
    string? BaseModelName = null);

/// <summary>Application-resolved, provider-revalidated import destination.</summary>
public sealed record GgufImportDestination(
    string CanonicalModelName,
    string CanonicalQuant,
    string RelativeGgufPath,
    string RelativeSidecarPath,
    LocalModelOrigin Origin,
    string? ProjectorRelativePath = null,
    TrainedModelLineage? Lineage = null)
{
    /// <summary>True when this destination commits a LoRA adapter rather than a standalone model.</summary>
    public bool IsAdapter => Lineage?.BaseModelName is { Length: > 0 };
}

/// <summary>Supported imported workload.</summary>
public enum GgufImportWorkload
{
    /// <summary>Causal/chat generation model.</summary>
    CausalChat = 0,

    /// <summary>
    ///     A LoRA adapter (GGUF <c>general.type == "adapter"</c>), which llama-server applies to a base model via
    ///     <c>--lora</c>. Only ever produced on the in-process path — see <see cref="GgufImportInspectionMode" />.
    /// </summary>
    LoraAdapter = 1
}

/// <summary>
///     Who is asking. The public HTTP import surface accepts exactly one thing — a standalone causal-chat model — and
///     leans on file-name heuristics as a second line of defence against an operator uploading a projector or adapter
///     under a misleading name. An in-process commit from a local training run needs neither: the engine wrote the file
///     it is committing, so the decision is made from the GGUF metadata alone and the name is not evidence of anything.
/// </summary>
public enum GgufImportInspectionMode
{
    /// <summary>Operator-supplied file arriving over the import surface. Today's behavior, unchanged.</summary>
    PublicImport = 0,

    /// <summary>Engine-produced artifact committed in-process by a training run's export.</summary>
    InProcessTrainedCommit = 1
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
    Task<GgufImportInspection> InspectAsync(GgufImportSource source,
        GgufImportInspectionMode mode,
        CancellationToken cancellationToken);

    /// <summary>Inspects under the strict public-import rules.</summary>
    Task<GgufImportInspection> InspectAsync(GgufImportSource source, CancellationToken cancellationToken) =>
        InspectAsync(source, GgufImportInspectionMode.PublicImport, cancellationToken);
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
    string ModelContentFingerprint)
{
    /// <summary>Whether this operation created the final weight path.</summary>
    public bool OwnsFinalGguf { get; init; } = true;

    /// <summary>Whether this operation created the final sidecar path.</summary>
    public bool OwnsFinalSidecar { get; init; } = true;
}

/// <summary>A failed commit that created one or more final artifacts which still require compensation.</summary>
public sealed class GgufImportCommitException : Exception
{
    /// <summary>Creates a partial-commit failure with the exact ownership receipt required for retryable rollback.</summary>
    public GgufImportCommitException(GgufImportCommitReceipt commitReceipt, string sanitizedMessage, Exception innerException)
        : base(sanitizedMessage, innerException)
    {
        CommitReceipt = commitReceipt;
    }

    /// <summary>Exact final paths created before the commit failed.</summary>
    public GgufImportCommitReceipt CommitReceipt { get; }
}

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
