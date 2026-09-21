namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

public enum DevWorkflowArtifactReadStatus
{
    Found,
    Missing,
    Tampered,
    SizeMismatch,
    HashMismatch
}

public sealed class DevWorkflowArtifactBlobWriteResult
{
    public required string OpaqueReference { get; init; }

    public required string ContentHash { get; init; }

    public required long ByteCount { get; init; }
}

public sealed class DevWorkflowArtifactBlobReadResult
{
    public required DevWorkflowArtifactReadStatus Status { get; init; }

    public required ReadOnlyMemory<byte> Content { get; init; }
}

/// <summary>
///     The bytes behind a workflow run's artifacts, encrypted at rest under the node key and keyed by run id.
/// </summary>
/// <remarks>
///     Rows live in <c>dev_workflow_artifacts</c>; only the digest and size cross between the two. Callers write the
///     blob <em>before</em> the row: a crash between the two leaks one bounded blob, where the other order would
///     leave a row pointing at bytes that never existed.
/// </remarks>
public interface IDevWorkflowArtifactBlobStore
{
    Task<DevWorkflowArtifactBlobWriteResult> WriteAsync(Guid runId,
        Guid artifactId,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default);

    Task<DevWorkflowArtifactBlobReadResult> ReadAsync(Guid runId,
        Guid artifactId,
        string expectedHash,
        long expectedByteCount,
        CancellationToken cancellationToken = default);

    /// <summary>Best-effort removal of one artifact's bytes. Never throws on a missing blob.</summary>
    void Delete(Guid runId, Guid artifactId);

    /// <summary>Best-effort removal of every artifact a deleted run owned. Never throws on a missing directory.</summary>
    void DeleteRun(Guid runId);

    /// <summary>The run ids with artifact bytes on disk whose newest write is older than <paramref name="cutoffUtc" />.</summary>
    /// <remarks>
    ///     Candidates for the orphan resweep in <c>RetentionSweeperService</c>: a run's row is committed by
    ///     <c>StartAsync</c> before any node can write an artifact, so a run id on disk with no row is a deleted
    ///     run's leftover bytes — what the best-effort <see cref="DeleteRun" /> beside the row delete failed to take.
    /// </remarks>
    IReadOnlyList<Guid> ListRunIdsLastWrittenBefore(DateTimeOffset cutoffUtc);
}
