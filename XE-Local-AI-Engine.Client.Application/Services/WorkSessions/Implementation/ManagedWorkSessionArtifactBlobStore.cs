namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Blobs;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Work-session artifacts on disk at
///     <c>{INodeDataDirectory.Root}/work-sessions/artifacts/{sessionId:N}/{artifactId:N}.blob</c>, encrypted under the
///     node key with the session and artifact ids bound into the AAD.
/// </summary>
public sealed class ManagedWorkSessionArtifactBlobStore : IWorkSessionArtifactBlobStore
{
    private readonly ManagedEncryptedBlobStore _blobs;

    public ManagedWorkSessionArtifactBlobStore(INodeDataDirectory dataDirectory, INodeSqliteKeyHolder keyHolder, IOptions<WorkSessionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _blobs = new ManagedEncryptedBlobStore(dataDirectory,
            keyHolder,
            "work-sessions",
            "artifacts",
            "work_session_artifact_blob",
            options.Value.MaxArtifactBytes,
            "work session artifact");
    }

    public async Task<WorkSessionArtifactBlobWriteResult> WriteAsync(Guid sessionId,
        Guid artifactId,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var written = await _blobs.WriteAsync(sessionId, artifactId, content, cancellationToken).ConfigureAwait(false);
        return new WorkSessionArtifactBlobWriteResult(written.OpaqueReference, written.ContentHash, written.ByteCount);
    }

    public async Task<WorkSessionArtifactBlobReadResult> ReadAsync(Guid sessionId,
        Guid artifactId,
        string expectedHash,
        long expectedByteCount,
        CancellationToken cancellationToken = default)
    {
        var read = await _blobs.ReadAsync(sessionId, artifactId, expectedHash, expectedByteCount, cancellationToken).ConfigureAwait(false);
        return new WorkSessionArtifactBlobReadResult(Map(read.Status), read.Content);
    }

    public void Delete(Guid sessionId, Guid artifactId)
    {
        _blobs.Delete(sessionId, artifactId);
    }

    public void DeleteSession(Guid sessionId)
    {
        _blobs.DeleteScope(sessionId);
    }

    private static WorkSessionArtifactReadStatus Map(ManagedBlobReadStatus status) =>
        status switch
        {
            ManagedBlobReadStatus.Found => WorkSessionArtifactReadStatus.Found,
            ManagedBlobReadStatus.Missing => WorkSessionArtifactReadStatus.Missing,
            ManagedBlobReadStatus.Tampered => WorkSessionArtifactReadStatus.Tampered,
            ManagedBlobReadStatus.SizeMismatch => WorkSessionArtifactReadStatus.SizeMismatch,
            _ => WorkSessionArtifactReadStatus.HashMismatch
        };
}
