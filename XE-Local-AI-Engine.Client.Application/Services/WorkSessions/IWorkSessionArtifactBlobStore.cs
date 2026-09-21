namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

public enum WorkSessionArtifactReadStatus
{
    Found,
    Missing,
    Tampered,
    SizeMismatch,
    HashMismatch
}

public sealed record WorkSessionArtifactBlobWriteResult
{
    public required string OpaqueReference { get; init; }

    public required string ContentHash { get; init; }

    public required long ByteCount { get; init; }
}

public sealed class WorkSessionArtifactBlobReadResult
{
    public required WorkSessionArtifactReadStatus Status { get; init; }

    public required ReadOnlyMemory<byte> Content { get; init; }
}

/// <summary>
///     The bytes behind a work session's artifacts, encrypted at rest under the node key and keyed by session id.
/// </summary>
/// <remarks>
///     Rows live in <c>agent_work_session_artifacts</c> and only the digest and size cross between the two. Callers
///     write the blob BEFORE the row: a crash between the two leaks one bounded blob, where the other order would
///     leave a row pointing at bytes that never existed.
/// </remarks>
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

    /// <summary>The session ids with artifact bytes on disk whose newest write is older than <paramref name="cutoffUtc" />.</summary>
    /// <remarks>
    ///     Candidates for the orphan resweep in <c>RetentionSweeperService</c>: a session's row is always committed
    ///     before it can hold an artifact, so a session id on disk with no row is a deleted session's leftover bytes.
    /// </remarks>
    IReadOnlyList<Guid> ListSessionIdsLastWrittenBefore(DateTimeOffset cutoffUtc);
}
