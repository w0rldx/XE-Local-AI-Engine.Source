namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

public enum WorkSessionArtifactReadStatus
{
    Found,
    Missing,
    Tampered,
    SizeMismatch,
    HashMismatch
}

public sealed record WorkSessionArtifactBlobWriteResult(string OpaqueReference, string ContentHash, long ByteCount);

public sealed record WorkSessionArtifactBlobReadResult(WorkSessionArtifactReadStatus Status, ReadOnlyMemory<byte> Content);

/// <summary>
///     The bytes behind a work session's artifacts, encrypted at rest under the node key and keyed by session id. Rows
///     live in <c>agent_work_session_artifacts</c>; only the digest and size cross between the two.
///     <para>
///         Callers write the blob <em>before</em> the row: a crash between the two leaks one bounded blob, where the
///         other order would leave a row pointing at bytes that never existed.
///     </para>
/// </summary>
public interface IWorkSessionArtifactBlobStore
{
    Task<WorkSessionArtifactBlobWriteResult> WriteAsync(Guid sessionId,
        Guid artifactId,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default);

    Task<WorkSessionArtifactBlobReadResult> ReadAsync(Guid sessionId,
        Guid artifactId,
        string expectedHash,
        long expectedByteCount,
        CancellationToken cancellationToken = default);

    /// <summary>Best-effort removal of one artifact's bytes, for a replaced artifact. Never throws on a missing blob.</summary>
    void Delete(Guid sessionId, Guid artifactId);

    /// <summary>Best-effort removal of every artifact a deleted session owned. Never throws on a missing directory.</summary>
    void DeleteSession(Guid sessionId);
}
