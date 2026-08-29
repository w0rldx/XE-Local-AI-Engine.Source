namespace XE_Local_AI_Engine.Client.Persistence.Tests.DevWorkflows;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Development;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

/// <summary>
///     The facade is seven lines of delegation over a primitive that is already tested, so the only thing that can be
///     wrong is the four constants it passes: the folder, the leaf, the AAD column and the cap. A round trip on disk is
///     what catches all four.
/// </summary>
public sealed class ManagedDevWorkflowArtifactBlobStoreTests : IDisposable
{
    private readonly NullNodeSqliteKeyHolder _keyHolder = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-dev-workflow-artifacts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task WriteReadAndTamper_UsesTheDocumentedRunScopedLayout()
    {
        var store = CreateStore();
        var runId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        ReadOnlyMemory<byte> content = "bounded dev workflow artifact"u8.ToArray();

        var written = await store.WriteAsync(runId, artifactId, content).ConfigureAwait(false);
        AssertEx.False(Path.IsPathRooted(written.OpaqueReference));
        AssertEx.False(written.OpaqueReference.Contains("..", StringComparison.Ordinal));

        var read = await store.ReadAsync(runId, artifactId, written.ContentHash, written.ByteCount).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowArtifactReadStatus.Found, read.Status);
        AssertEx.True(read.Content.Span.SequenceEqual(content.Span));

        // The scope is the RUN, not the work item: staleness, replay and teardown are all run-scoped in v1.
        var path = Path.Combine(_root, "dev-workflows", "artifacts", runId.ToString("N"), artifactId.ToString("N") + ".blob");
        AssertEx.True(File.Exists(path), $"The blob must live at {path}.");

        AssertEx.Equal(DevWorkflowArtifactReadStatus.HashMismatch,
            (await store.ReadAsync(runId, artifactId, new string('0', count: 64), written.ByteCount).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowArtifactReadStatus.SizeMismatch,
            (await store.ReadAsync(runId, artifactId, written.ContentHash, written.ByteCount + 1).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowArtifactReadStatus.Missing,
            (await store.ReadAsync(runId, Guid.NewGuid(), written.ContentHash, written.ByteCount).ConfigureAwait(false)).Status);

        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        bytes[^1] ^= 0x5A;
        await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowArtifactReadStatus.Tampered,
            (await store.ReadAsync(runId, artifactId, written.ContentHash, written.ByteCount).ConfigureAwait(false)).Status);
    }

    [Test]
    public async Task DeletingARun_TakesItsWholeArtifactDirectoryAndSurvivesAnAbsentOne()
    {
        var store = CreateStore();
        var runId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        store.Delete(runId, artifactId);
        store.DeleteRun(runId);

        var written = await store.WriteAsync(runId, artifactId, "content"u8.ToArray()).ConfigureAwait(false);
        store.Delete(runId, artifactId);
        AssertEx.Equal(DevWorkflowArtifactReadStatus.Missing,
            (await store.ReadAsync(runId, artifactId, written.ContentHash, written.ByteCount).ConfigureAwait(false)).Status);

        _ = await store.WriteAsync(runId, artifactId, "content"u8.ToArray()).ConfigureAwait(false);
        store.DeleteRun(runId);
        AssertEx.False(Directory.Exists(Path.Combine(_root, "dev-workflows", "artifacts", runId.ToString("N"))),
            "The row purge is the caller's; taking the bytes with the run is this store's half of it.");
    }

    [Test]
    public async Task OversizedWrite_FailsBeforeAnythingReachesDisk()
    {
        var store = CreateStore(maxArtifactBytes: 4);

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(Guid.NewGuid(), Guid.NewGuid(), new byte[5])).ConfigureAwait(false);
        AssertEx.False(Directory.Exists(Path.Combine(_root, "dev-workflows")), "A rejected write must not create the artifact tree.");
    }

    private ManagedDevWorkflowArtifactBlobStore CreateStore(int maxArtifactBytes = 1024 * 1024) =>
        new(new TestDataDirectory(_root),
            _keyHolder,
            Options.Create(new DevWorkflowOptions
            {
                Enabled = true,
                MaxArtifactBytes = maxArtifactBytes
            }));
}
