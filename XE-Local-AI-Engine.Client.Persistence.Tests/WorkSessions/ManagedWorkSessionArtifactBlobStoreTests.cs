namespace XE_Local_AI_Engine.Client.Persistence.Tests.WorkSessions;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Development;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

[Category(TestCategories.Unit)]
public sealed class ManagedWorkSessionArtifactBlobStoreTests : IDisposable
{
    private readonly NullNodeSqliteKeyHolder _keyHolder = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-work-session-artifacts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task WriteReadAndTamper_UsesTheDocumentedImmutableHashVerifiedLayout()
    {
        var store = CreateStore();
        var sessionId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        ReadOnlyMemory<byte> content = "bounded work session artifact"u8.ToArray();

        var written = await store.WriteAsync(sessionId, artifactId, content);
        var replay = await store.WriteAsync(sessionId, artifactId, content);
        AssertEx.Equal(written, replay);
        _ = await AssertEx.ThrowsAsync<IOException>(() => store.WriteAsync(sessionId, artifactId, "different"u8.ToArray()));
        AssertEx.False(Path.IsPathRooted(written.OpaqueReference));
        AssertEx.False(written.OpaqueReference.Contains("..", StringComparison.Ordinal));

        var read = await store.ReadAsync(sessionId, artifactId, written.ContentHash, written.ByteCount);
        AssertEx.Equal(WorkSessionArtifactReadStatus.Found, read.Status);
        AssertEx.True(read.Content.Span.SequenceEqual(content.Span));

        // The one authoritative on-disk layout: scope id after the leaf segment, matching the shared helper.
        var path = Path.Combine(_root, "work-sessions", "artifacts", sessionId.ToString("N"), artifactId.ToString("N") + ".blob");
        AssertEx.True(File.Exists(path), $"The blob must live at {path}.");

        AssertEx.Equal(WorkSessionArtifactReadStatus.HashMismatch,
            (await store.ReadAsync(sessionId, artifactId, new string('0', count: 64), written.ByteCount)).Status);
        AssertEx.Equal(WorkSessionArtifactReadStatus.SizeMismatch,
            (await store.ReadAsync(sessionId, artifactId, written.ContentHash, written.ByteCount + 1)).Status);
        AssertEx.Equal(WorkSessionArtifactReadStatus.Missing,
            (await store.ReadAsync(sessionId, Guid.NewGuid(), written.ContentHash, written.ByteCount)).Status);

        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 0x5A;
        await File.WriteAllBytesAsync(path, bytes);
        AssertEx.Equal(WorkSessionArtifactReadStatus.Tampered,
            (await store.ReadAsync(sessionId, artifactId, written.ContentHash, written.ByteCount)).Status);
    }

    [Test]
    public async Task OversizedWrite_FailsBeforeAnythingReachesDisk()
    {
        var store = CreateStore(maxArtifactBytes: 4);

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(Guid.NewGuid(), Guid.NewGuid(), new byte[5]));
        AssertEx.False(Directory.Exists(Path.Combine(_root, "work-sessions")), "A rejected write must not create the artifact tree.");
    }

    [Test]
    public async Task DefaultCap_IsOneMebibyte()
    {
        var store = CreateStore();
        var options = new WorkSessionOptions();
        AssertEx.Equal(expected: 1024 * 1024, options.MaxArtifactBytes);

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(Guid.NewGuid(), Guid.NewGuid(), new byte[options.MaxArtifactBytes + 1]));
    }

    [Test]
    public async Task Delete_IsBestEffortAndSurvivesAnAbsentBlob()
    {
        var store = CreateStore();
        var sessionId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        store.Delete(sessionId, artifactId);
        store.DeleteSession(sessionId);

        var written = await store.WriteAsync(sessionId, artifactId, "content"u8.ToArray());
        store.Delete(sessionId, artifactId);
        AssertEx.Equal(WorkSessionArtifactReadStatus.Missing,
            (await store.ReadAsync(sessionId, artifactId, written.ContentHash, written.ByteCount)).Status);

        _ = await store.WriteAsync(sessionId, artifactId, "content"u8.ToArray());
        store.DeleteSession(sessionId);
        AssertEx.False(Directory.Exists(Path.Combine(_root, "work-sessions", "artifacts", sessionId.ToString("N"))),
            "Deleting a session must take its whole artifact directory.");
    }

    private ManagedWorkSessionArtifactBlobStore CreateStore(int maxArtifactBytes = 1024 * 1024) =>
        new(new TestDataDirectory(_root),
            _keyHolder,
            Options.Create(new WorkSessionOptions
            {
                Enabled = true,
                MaxArtifactBytes = maxArtifactBytes
            }));
}
