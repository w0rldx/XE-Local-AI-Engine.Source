namespace XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Blobs;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Run artifacts on disk at <c>{INodeDataDirectory.Root}/benchmarks/artifacts/{runId:N}/{artifactId:N}.blob</c>,
///     encrypted under the node key with the run and artifact ids bound into the AAD.
/// </summary>
/// <remarks>
///     The crypto, atomic-write and tamper-classification body lives in <see cref="ManagedEncryptedBlobStore" />;
///     this is the folder, leaf, AAD column and cap. Content is raw and candidate-controlled.
/// </remarks>
public sealed class BenchmarkRunArtifactBlobStore
{
    /// <summary>The per-artifact cap. Sandbox streams can be larger (MiBs), so the producer truncates to fit.</summary>
    public const int MaxArtifactBytes = 256 * 1024;

    private readonly ManagedEncryptedBlobStore _blobs;

    public BenchmarkRunArtifactBlobStore(INodeDataDirectory dataDirectory, INodeSqliteKeyHolder keyHolder)
    {
        _blobs = new ManagedEncryptedBlobStore(dataDirectory,
            keyHolder,
            "benchmarks",
            "artifacts",
            "benchmark_run_artifact_blob",
            MaxArtifactBytes,
            "benchmark run artifact");
    }

    /// <summary>Writes one immutable artifact and returns the SHA-256 hex of its content, which its row records.</summary>
    public async Task<string> WriteAsync(Guid runId, Guid artifactId, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        var written = await _blobs.WriteAsync(runId, artifactId, content, cancellationToken);
        return written.ContentHash;
    }

    public async Task<BenchmarkRunArtifactBlobReadResult> ReadAsync(Guid runId,
        Guid artifactId,
        string expectedHash,
        long expectedByteCount,
        CancellationToken cancellationToken = default)
    {
        var read = await _blobs.ReadAsync(runId, artifactId, expectedHash, expectedByteCount, cancellationToken);
        return new BenchmarkRunArtifactBlobReadResult
        {
            Status = read.Status switch
            {
                ManagedBlobReadStatus.Found => BenchmarkRunArtifactBlobReadStatus.Found,
                ManagedBlobReadStatus.Missing => BenchmarkRunArtifactBlobReadStatus.Missing,
                ManagedBlobReadStatus.Tampered => BenchmarkRunArtifactBlobReadStatus.Tampered,
                ManagedBlobReadStatus.SizeMismatch => BenchmarkRunArtifactBlobReadStatus.SizeMismatch,
                _ => BenchmarkRunArtifactBlobReadStatus.HashMismatch
            },
            Content = read.Content
        };
    }

    /// <summary>Best-effort removal of a deleted run's whole artifact directory; never throws.</summary>
    public void DeleteRun(Guid runId)
    {
        _blobs.DeleteScope(runId);
    }

    public IReadOnlyList<Guid> ListRunIdsLastWrittenBefore(DateTimeOffset cutoffUtc)
    {
        return _blobs.ListScopeIdsLastWrittenBefore(cutoffUtc);
    }
}
