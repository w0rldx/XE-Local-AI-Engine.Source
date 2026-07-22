namespace XE_Local_AI_Engine.Client.Persistence.Tests.Development;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Providers.Abstractions;

public sealed class ManagedDevelopmentArtifactStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-artifacts-" + Guid.NewGuid().ToString("N"));
    private readonly NullNodeSqliteKeyHolder _keyHolder = new();

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task WriteReadAndTamper_UsesOpaqueImmutableHashVerifiedArtifacts()
    {
        var store = new ManagedDevelopmentArtifactBlobStore(new TestDataDirectory(_root),
            _keyHolder,
            Options.Create(new DevelopmentOptions { Enabled = true, MaxArtifactBytes = 1024 }));
        var projectId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        ReadOnlyMemory<byte> content = "bounded artifact"u8.ToArray();

        var written = await store.WriteAsync(projectId, artifactId, content).ConfigureAwait(false);
        var replay = await store.WriteAsync(projectId, artifactId, content).ConfigureAwait(false);
        AssertEx.Equal(written, replay);
        await AssertEx.ThrowsAsync<IOException>(() => store.WriteAsync(projectId, artifactId, "different"u8.ToArray()));
        AssertEx.False(Path.IsPathRooted(written.OpaqueReference));
        AssertEx.False(written.OpaqueReference.Contains("..", StringComparison.Ordinal));

        var read = await store.ReadAsync(projectId, artifactId, written.ContentHash, written.ByteCount).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentArtifactReadStatus.Found, read.Status);
        AssertEx.True(read.Content.Span.SequenceEqual(content.Span));

        var path = Path.Combine(_root, "development", "artifacts", projectId.ToString("N"), artifactId.ToString("N") + ".blob");
        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        bytes[^1] ^= 0x5A;
        await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
        var tampered = await store.ReadAsync(projectId, artifactId, written.ContentHash, written.ByteCount).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentArtifactReadStatus.Tampered, tampered.Status);
    }

    [Test]
    public async Task OversizedWrite_FailsBeforeFinalOrTemporaryArtifactExists()
    {
        var store = new ManagedDevelopmentArtifactBlobStore(new TestDataDirectory(_root),
            _keyHolder,
            Options.Create(new DevelopmentOptions { Enabled = true, MaxArtifactBytes = 4 }));
        await AssertEx.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(Guid.NewGuid(), Guid.NewGuid(), new byte[5]))
                      .ConfigureAwait(false);
        AssertEx.False(Directory.Exists(Path.Combine(_root, "development")));
    }

    [Test]
    public async Task AttachArtifact_RejectsCallerProvidedManagedReference()
    {
        var fixture = new DevelopmentTestFixture();
        try
        {
            await using var provider = await fixture.BuildProviderAsync().ConfigureAwait(false);
            await using var scope = provider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
            var seed = DevelopmentTestFixture.CreateSeed();
            _ = await store.CreateProjectAsync(seed).ConfigureAwait(false);

            await AssertEx.ThrowsAsync<ArgumentException>(() => store.AttachArtifactAsync(new DevelopmentAttachArtifactCommand(Guid.NewGuid(),
                                                                                                      seed.ProjectId,
                                                                                                      seed.TaskId,
                                                                                                      AttemptId: null,
                                                                                                      Guid.NewGuid(),
                                                                                                      DevelopmentArtifactKind.Patch,
                                                                                                      SchemaVersion: 1,
                                                                                                      ContentHash: "hash",
                                                                                                      ByteCount: 1,
                                                                                                      ManagedReference: "../../caller/path")))
                          .ConfigureAwait(false);
        }
        finally
        {
            fixture.Dispose();
        }
    }
}
