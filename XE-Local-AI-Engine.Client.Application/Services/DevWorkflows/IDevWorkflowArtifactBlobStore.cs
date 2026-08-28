namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

public enum DevWorkflowArtifactReadStatus
{
    Found,
    Missing,
    Tampered,
    SizeMismatch,
    HashMismatch
}

public sealed record DevWorkflowArtifactBlobWriteResult(string OpaqueReference, string ContentHash, long ByteCount);

public sealed record DevWorkflowArtifactBlobReadResult(DevWorkflowArtifactReadStatus Status, ReadOnlyMemory<byte> Content);

/// <summary>
///     The bytes behind a workflow run's artifacts, encrypted at rest under the node key and keyed by run id. Rows live
///     in <c>dev_workflow_artifacts</c>; only the digest and size cross between the two.
///     <para>
///         Callers write the blob <em>before</em> the row: a crash between the two leaks one bounded blob, where the
///         other order would leave a row pointing at bytes that never existed.
///     </para>
/// </summary>
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
}
