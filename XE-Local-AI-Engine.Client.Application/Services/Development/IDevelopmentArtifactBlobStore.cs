namespace XE_Local_AI_Engine.Client.Services.Development;

public enum DevelopmentArtifactReadStatus
{
    Found,
    Missing,
    Tampered,
    SizeMismatch,
    HashMismatch
}

public sealed record DevelopmentArtifactBlobWriteResult(string OpaqueReference, string ContentHash, long ByteCount);

public sealed record DevelopmentArtifactBlobReadResult(DevelopmentArtifactReadStatus Status, ReadOnlyMemory<byte> Content)
{
    public static DevelopmentArtifactBlobReadResult Failure(DevelopmentArtifactReadStatus status) =>
        new(status, ReadOnlyMemory<byte>.Empty);
}

public interface IDevelopmentArtifactBlobStore
{
    Task<DevelopmentArtifactBlobWriteResult> WriteAsync(Guid projectId,
        Guid artifactId,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default);

    Task<DevelopmentArtifactBlobReadResult> ReadAsync(Guid projectId,
        Guid artifactId,
        string expectedHash,
        long expectedByteCount,
        CancellationToken cancellationToken = default);
}
