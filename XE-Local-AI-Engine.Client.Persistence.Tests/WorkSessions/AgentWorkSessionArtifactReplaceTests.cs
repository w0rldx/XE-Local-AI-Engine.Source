namespace XE_Local_AI_Engine.Client.Persistence.Tests.WorkSessions;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Development;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

public sealed class AgentWorkSessionArtifactReplaceTests
{
    [Test]
    public async Task SavingTwiceUnderOneName_LeavesOneRowAndNoOrphanBlob()
    {
        using var fixture = new WorkSessionTestFixture();
        using var blobKeyHolder = new NullNodeSqliteKeyHolder();
        var blobs = new ManagedWorkSessionArtifactBlobStore(new TestDataDirectory(fixture.Root),
            blobKeyHolder,
            Options.Create(new WorkSessionOptions
            {
                Enabled = true
            }));
        var sessionId = Guid.NewGuid();
        var firstArtifactId = Guid.NewGuid();
        var secondArtifactId = Guid.NewGuid();

        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = WorkSessionTestFixture.StoreFor(context);
        var created = await WorkSessionTestFixture.SeedAsync(store, sessionId).ConfigureAwait(false);

        var first = await SaveAsync(store, blobs, sessionId, firstArtifactId, created.Version, "report.md", "first draft"u8.ToArray()).ConfigureAwait(false);
        AssertEx.Null(first.SupersededArtifactId, "The first save replaces nothing.");

        var second = await SaveAsync(store, blobs, sessionId, secondArtifactId, first.Version, "report.md", "second draft"u8.ToArray()).ConfigureAwait(false);
        AssertEx.Equal(firstArtifactId, second.SupersededArtifactId, "A save under an existing name must report what it replaced.");

        // The store cannot reach the blob layer, so the caller sweeps the bytes with the id the result handed back.
        blobs.Delete(sessionId, second.SupersededArtifactId!.Value);

        var artifacts = await store.ListArtifactsAsync(sessionId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, artifacts.Count);
        AssertEx.Equal(secondArtifactId, artifacts[0].Id);

        var read = await blobs.ReadAsync(sessionId, secondArtifactId, artifacts[0].ContentSha256, artifacts[0].SizeBytes).ConfigureAwait(false);
        AssertEx.Equal(WorkSessionArtifactReadStatus.Found, read.Status);
        AssertEx.True(read.Content.Span.SequenceEqual("second draft"u8), "The surviving row must resolve to the newer content.");

        AssertEx.False(File.Exists(Path.Combine(fixture.Root, "work-sessions", "artifacts", sessionId.ToString("N"), firstArtifactId.ToString("N") + ".blob")),
            "The superseded artifact's bytes must not be left behind.");

        var replaceEvent = (await store.ListEventsAsync(sessionId).ConfigureAwait(false)).Last(entry => entry.EventType == "ArtifactSaved");
        var detail = AssertEx.NotNull(replaceEvent.DetailJson, "The replace event must carry the superseded reference.");
        AssertEx.True(detail.Contains(firstArtifactId.ToString(), StringComparison.OrdinalIgnoreCase), "The superseded artifact id belongs on the event.");
    }

    [Test]
    public async Task ReusingAnArtifactId_IsRefused()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var created = await WorkSessionTestFixture.SeedAsync(store, sessionId).ConfigureAwait(false);

        var saved = await store.AppendArtifactAsync(Command(sessionId, artifactId, created.Version, "first.md", "HASH", size: 1)).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<WorkSessionConcurrencyException>(() =>
                              store.AppendArtifactAsync(Command(sessionId, artifactId, saved.Version, "second.md", "HASH", size: 1)))
                          .ConfigureAwait(false);
    }

    private static async Task<WorkSessionMutationResult> SaveAsync(AgentWorkSessionStore store,
        IWorkSessionArtifactBlobStore blobs,
        Guid sessionId,
        Guid artifactId,
        long expectedVersion,
        string name,
        ReadOnlyMemory<byte> content)
    {
        // Blob first, then the row: a crash between the two leaks one bounded blob, where the other order would leave a
        // row pointing at bytes that never existed.
        var written = await blobs.WriteAsync(sessionId, artifactId, content).ConfigureAwait(false);
        return await store.AppendArtifactAsync(Command(sessionId, artifactId, expectedVersion, name, written.ContentHash, written.ByteCount)).ConfigureAwait(false);
    }

    private static AppendWorkSessionArtifactCommand Command(Guid sessionId, Guid artifactId, long expectedVersion, string name, string hash, long size) =>
        new(sessionId,
            artifactId,
            expectedVersion,
            Guid.NewGuid(),
            AgentWorkSessionArtifactKind.Report,
            name,
            "text/markdown",
            hash,
            size,
            string.Concat(sessionId.ToString("N"), "/", artifactId.ToString("N")));
}
