namespace XE_Local_AI_Engine.Client.Services.Development;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Blobs;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Development Mode's managed artifact blobs. The crypto, atomic-write and tamper-classification body lives in
///     <see cref="ManagedEncryptedBlobStore" />; the folder, leaf and AAD column here are exactly the values this store
///     has always used, so every blob already on disk stays readable.
/// </summary>
public sealed class ManagedDevelopmentArtifactBlobStore : IDevelopmentArtifactBlobStore
{
    private readonly ManagedEncryptedBlobStore _blobs;

    public ManagedDevelopmentArtifactBlobStore(INodeDataDirectory dataDirectory, INodeSqliteKeyHolder keyHolder, IOptions<DevelopmentOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _blobs = new ManagedEncryptedBlobStore(dataDirectory,
            keyHolder,
            "development",
            "artifacts",
            "development_artifact_blob",
            options.Value.MaxArtifactBytes,
            "Development artifact");
    }

    public async Task<DevelopmentArtifactBlobWriteResult> WriteAsync(Guid projectId,
        Guid artifactId,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var written = await _blobs.WriteAsync(projectId, artifactId, content, cancellationToken).ConfigureAwait(false);
        return new DevelopmentArtifactBlobWriteResult(written.OpaqueReference, written.ContentHash, written.ByteCount);
    }

    public async Task<DevelopmentArtifactBlobReadResult> ReadAsync(Guid projectId,
        Guid artifactId,
        string expectedHash,
        long expectedByteCount,
        CancellationToken cancellationToken = default)
    {
        var read = await _blobs.ReadAsync(projectId, artifactId, expectedHash, expectedByteCount, cancellationToken).ConfigureAwait(false);
        return new DevelopmentArtifactBlobReadResult(Map(read.Status), read.Content);
    }

    private static DevelopmentArtifactReadStatus Map(ManagedBlobReadStatus status) =>
        status switch
        {
            ManagedBlobReadStatus.Found => DevelopmentArtifactReadStatus.Found,
            ManagedBlobReadStatus.Missing => DevelopmentArtifactReadStatus.Missing,
            ManagedBlobReadStatus.Tampered => DevelopmentArtifactReadStatus.Tampered,
            ManagedBlobReadStatus.SizeMismatch => DevelopmentArtifactReadStatus.SizeMismatch,
            _ => DevelopmentArtifactReadStatus.HashMismatch
        };
}
