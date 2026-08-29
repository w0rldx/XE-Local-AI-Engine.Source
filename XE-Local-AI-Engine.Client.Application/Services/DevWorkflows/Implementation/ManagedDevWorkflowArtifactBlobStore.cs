namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Blobs;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Workflow artifacts on disk at
///     <c>{INodeDataDirectory.Root}/dev-workflows/artifacts/{runId:N}/{artifactId:N}.blob</c>, encrypted under the node
///     key with the run and artifact ids bound into the AAD. The crypto, atomic-write and tamper-classification body
///     lives in <see cref="ManagedEncryptedBlobStore" />; this is the folder, leaf and AAD column, and nothing else.
/// </summary>
public sealed class ManagedDevWorkflowArtifactBlobStore : IDevWorkflowArtifactBlobStore
{
    private readonly ManagedEncryptedBlobStore _blobs;

    public ManagedDevWorkflowArtifactBlobStore(INodeDataDirectory dataDirectory, INodeSqliteKeyHolder keyHolder, IOptions<DevWorkflowOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _blobs = new ManagedEncryptedBlobStore(dataDirectory,
            keyHolder,
            "dev-workflows",
            "artifacts",
            "dev_workflow_artifact_blob",
            options.Value.MaxArtifactBytes,
            "dev workflow artifact");
    }

    public async Task<DevWorkflowArtifactBlobWriteResult> WriteAsync(Guid runId,
        Guid artifactId,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var written = await _blobs.WriteAsync(runId, artifactId, content, cancellationToken).ConfigureAwait(false);
        return new DevWorkflowArtifactBlobWriteResult(written.OpaqueReference, written.ContentHash, written.ByteCount);
    }

    public async Task<DevWorkflowArtifactBlobReadResult> ReadAsync(Guid runId,
        Guid artifactId,
        string expectedHash,
        long expectedByteCount,
        CancellationToken cancellationToken = default)
    {
        var read = await _blobs.ReadAsync(runId, artifactId, expectedHash, expectedByteCount, cancellationToken).ConfigureAwait(false);
        return new DevWorkflowArtifactBlobReadResult(Map(read.Status), read.Content);
    }

    public void Delete(Guid runId, Guid artifactId)
    {
        _blobs.Delete(runId, artifactId);
    }

    public void DeleteRun(Guid runId)
    {
        _blobs.DeleteScope(runId);
    }

    private static DevWorkflowArtifactReadStatus Map(ManagedBlobReadStatus status) =>
        status switch
        {
            ManagedBlobReadStatus.Found => DevWorkflowArtifactReadStatus.Found,
            ManagedBlobReadStatus.Missing => DevWorkflowArtifactReadStatus.Missing,
            ManagedBlobReadStatus.Tampered => DevWorkflowArtifactReadStatus.Tampered,
            ManagedBlobReadStatus.SizeMismatch => DevWorkflowArtifactReadStatus.SizeMismatch,
            _ => DevWorkflowArtifactReadStatus.HashMismatch
        };
}
