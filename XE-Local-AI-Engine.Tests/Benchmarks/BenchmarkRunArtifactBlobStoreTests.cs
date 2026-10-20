namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The wrapper over the managed blob store, on a real scratch data directory: what can be wrong is the folder,
///     leaf, AAD column and cap it passes, and a round trip on disk catches all four.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class BenchmarkRunArtifactBlobStoreTests : IDisposable
{
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("Traceback (most recent call last): candidate-controlled ✓\n");

    private readonly NullNodeSqliteKeyHolder _keyHolder = new();
    private readonly TempDirectory _root = new("xe-benchmark-artifacts");
    private readonly BenchmarkRunArtifactBlobStore _store;

    public BenchmarkRunArtifactBlobStoreTests()
    {
        _store = new BenchmarkRunArtifactBlobStore(new FakeNodeDataDirectory(_root.Path), _keyHolder);
    }

    public void Dispose()
    {
        _root.Dispose();
        _keyHolder.Dispose();
    }

    [Test]
    public async Task Write_ThenRead_RoundTripsExactBytes()
    {
        var runId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        var hash = await _store.WriteAsync(runId, artifactId, Content);
        var read = await _store.ReadAsync(runId, artifactId, hash, Content.Length);

        AssertEx.Equal(BenchmarkRunArtifactBlobReadStatus.Found, read.Status);
        AssertEx.True(read.Content.Span.SequenceEqual(Content));
        AssertEx.True(File.Exists(BlobPath(runId, artifactId)), "The blob lives at benchmarks/artifacts/{runId:N}/{artifactId:N}.blob.");
        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
            _store.WriteAsync(runId, Guid.NewGuid(), new byte[BenchmarkRunArtifactBlobStore.MaxArtifactBytes + 1]));
    }

    [Test]
    public async Task Read_WhenBlobBelongsToAnotherRun_IsTampered()
    {
        // The AAD binds the run id, so a valid ciphertext moved under another run must not decrypt there.
        var runId = Guid.NewGuid();
        var otherRunId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var hash = await _store.WriteAsync(runId, artifactId, Content);
        Directory.CreateDirectory(Path.GetDirectoryName(BlobPath(otherRunId, artifactId))!);
        File.Copy(BlobPath(runId, artifactId), BlobPath(otherRunId, artifactId));

        var read = await _store.ReadAsync(otherRunId, artifactId, hash, Content.Length);

        AssertEx.Equal(BenchmarkRunArtifactBlobReadStatus.Tampered, read.Status);
        AssertEx.True(read.Content.IsEmpty);
    }

    [Test]
    public async Task File_IsCiphertextAtRest()
    {
        var runId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        _ = await _store.WriteAsync(runId, artifactId, Content);

        var onDisk = await File.ReadAllBytesAsync(BlobPath(runId, artifactId));

        AssertEx.True(onDisk.Length > Content.Length, "The file carries a nonce and a tag beside the ciphertext.");
        AssertEx.Equal(-1, onDisk.AsSpan().IndexOf(Encoding.UTF8.GetBytes("candidate-controlled")), "No plaintext may reach the disk.");
    }

    [Test]
    public async Task DeleteRun_RemovesTheScopeDirectory()
    {
        var runId = Guid.NewGuid();
        var keptRunId = Guid.NewGuid();
        _ = await _store.WriteAsync(runId, Guid.NewGuid(), Content);
        _ = await _store.WriteAsync(runId, Guid.NewGuid(), Content);
        var keptId = Guid.NewGuid();
        _ = await _store.WriteAsync(keptRunId, keptId, Content);

        _store.DeleteRun(runId);
        _store.DeleteRun(Guid.NewGuid());

        AssertEx.False(Directory.Exists(Path.GetDirectoryName(BlobPath(runId, Guid.Empty))), "The whole run directory goes.");
        AssertEx.True(File.Exists(BlobPath(keptRunId, keptId)), "Another run's artifacts are untouched.");
    }

    private string BlobPath(Guid runId, Guid artifactId) =>
        Path.Combine(_root.Path, "benchmarks", "artifacts", runId.ToString("N"), string.Concat(artifactId.ToString("N"), ".blob"));
}
