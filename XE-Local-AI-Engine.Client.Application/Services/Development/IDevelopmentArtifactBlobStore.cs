namespace XE_Local_AI_Engine.Client.Services.Development;

public enum DevelopmentArtifactReadStatus
{
    Found,
    Missing,
    Tampered,
    SizeMismatch,
    HashMismatch
}

public sealed record DevelopmentArtifactBlobWriteResult
{
    public required string OpaqueReference { get; init; }

    public required string ContentHash { get; init; }

    public required long ByteCount { get; init; }
}

public sealed class DevelopmentArtifactBlobReadResult
{
    public required DevelopmentArtifactReadStatus Status { get; init; }

    public required ReadOnlyMemory<byte> Content { get; init; }

    public static DevelopmentArtifactBlobReadResult Failure(DevelopmentArtifactReadStatus status) =>
        new() { Status = status, Content = ReadOnlyMemory<byte>.Empty };
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
