namespace XE_Local_AI_Engine.Client.Persistence.Tests.Training;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Exercises the base-artifact store against the real SQLite schema, which is where the
///     <c>ux_training_base_artifacts_repo_revision</c> unique index actually lives — the retry behaviour it forces
///     cannot be observed against an in-memory substitute.
/// </summary>
public sealed class TrainingBaseArtifactStoreTests : IDisposable
{
    private const string RepoId = "unsloth/Llama-3.2-1B-Instruct";
    private const string Revision = "abc123";

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task StartDownload_AfterAFailure_ResetsTheExistingRowRatherThanInsertingASecond()
    {
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        await using var context = await CreateContextAsync("retry.sqlite", keyHolder);
        var store = new TrainingBaseArtifactStore(context, TimeProvider.System);

        var first = await store.StartDownloadAsync(RepoId, Revision);
        _ = await store.MarkFailedAsync(first.Id, first.Version, "The download failed.");

        var retried = await store.StartDownloadAsync(RepoId, Revision);

        // (repo_id, revision) is UNIQUE: a second insert would violate it, so the retry has to reset in place.
        AssertEx.Equal(first.Id, retried.Id);
        AssertEx.Equal(TrainingBaseArtifactStatus.Downloading, retried.Status);
        AssertEx.Null(retried.ErrorMessage);
        AssertEx.Equal(1, await context.TrainingBaseArtifacts.CountAsync());
        AssertEx.True(retried.Version > first.Version, "A reset is a mutation and must bump the concurrency token.");
    }

    [Test]
    public async Task StartDownload_WhileAlreadyDownloading_ReturnsTheSameRowUntouched()
    {
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        await using var context = await CreateContextAsync("double-submit.sqlite", keyHolder);
        var store = new TrainingBaseArtifactStore(context, TimeProvider.System);

        var first = await store.StartDownloadAsync(RepoId, Revision);
        var second = await store.StartDownloadAsync(RepoId, Revision);

        AssertEx.Equal(first.Id, second.Id);
        AssertEx.Equal(first.Version, second.Version, "A double-submit must not restart an in-flight transfer.");
    }

    [Test]
    public async Task MarkReady_StoresTheManifestAndLicenseAndBumpsTheVersion()
    {
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        await using var context = await CreateContextAsync("ready.sqlite", keyHolder);
        var store = new TrainingBaseArtifactStore(context, TimeProvider.System);

        var started = await store.StartDownloadAsync(RepoId, Revision);
        var files = Encoding.UTF8.GetBytes("""[{"role":"Weights","fileName":"model.safetensors"}]""");
        var license = Encoding.UTF8.GetBytes("""{"license":"llama3.2"}""");

        var ready = await store.MarkReadyAsync(started.Id, started.Version, files, totalBytes: 4096, license);

        AssertEx.Equal(TrainingBaseArtifactStatus.Ready, ready.Status);
        AssertEx.Equal(4096, ready.TotalBytes);
        AssertEx.Equal(Encoding.UTF8.GetString(files), Encoding.UTF8.GetString(ready.FilesJson.Span));
        AssertEx.True(ready.Version > started.Version);
    }

    [Test]
    public async Task MarkReady_WithAStaleVersion_IsRefused()
    {
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        await using var context = await CreateContextAsync("stale.sqlite", keyHolder);
        var store = new TrainingBaseArtifactStore(context, TimeProvider.System);

        var started = await store.StartDownloadAsync(RepoId, Revision);

        _ = await AssertEx.ThrowsAsync<TrainingBaseArtifactConcurrencyException>(
            () => store.MarkReadyAsync(started.Id, started.Version + 5, [], totalBytes: 0, licenseJson: null));
    }

    [Test]
    public async Task RecoverOnStartup_TerminalizesRowsStrandedByAnInterruptedProcess()
    {
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        await using var context = await CreateContextAsync("recover.sqlite", keyHolder);
        var store = new TrainingBaseArtifactStore(context, TimeProvider.System);

        var stranded = await store.StartDownloadAsync(RepoId, Revision);
        var ready = await store.StartDownloadAsync("other/repo", Revision);
        _ = await store.MarkReadyAsync(ready.Id, ready.Version, [], totalBytes: 0, licenseJson: null);

        AssertEx.Equal(1, await store.RecoverOnStartupAsync());

        // Nothing else would ever move a stranded row off Downloading, and the delete guard would refuse forever.
        var recovered = AssertEx.NotNull(await store.GetAsync(stranded.Id), "The stranded row must still exist.");
        AssertEx.Equal(TrainingBaseArtifactStatus.Failed, recovered.Status);
        AssertEx.NotNull(recovered.ErrorMessage, "A terminalized row must say why.");

        var untouched = AssertEx.NotNull(await store.GetAsync(ready.Id), "A ready row must not be touched.");
        AssertEx.Equal(TrainingBaseArtifactStatus.Ready, untouched.Status);
    }

    [Test]
    public async Task Delete_RemovesTheRowAndReportsFalseForAnUnknownId()
    {
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        await using var context = await CreateContextAsync("delete.sqlite", keyHolder);
        var store = new TrainingBaseArtifactStore(context, TimeProvider.System);

        var started = await store.StartDownloadAsync(RepoId, Revision);

        AssertEx.False(await store.DeleteAsync(Guid.NewGuid(), expectedVersion: 1));
        AssertEx.True(await store.DeleteAsync(started.Id, started.Version));
        AssertEx.Equal(0, await context.TrainingBaseArtifacts.CountAsync());
    }

    private async Task<NodeChatDbContext> CreateContextAsync(string fileName, FixedNodeSqliteKeyHolder keyHolder)
    {
        _ = Directory.CreateDirectory(_rootPath);
        var context = AgentDefinitionTestContextFactory.Create(Path.Combine(_rootPath, fileName), keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    private static byte[] CreateKeyMaterial()
    {
        return RandomNumberGenerator.GetBytes(32);
    }

    private sealed class FixedNodeSqliteKeyHolder(byte[] key) : INodeSqliteKeyHolder
    {
        private byte[]? _key = key;

        public ReadOnlyMemory<byte> Key
        {
            get
            {
                ObjectDisposedException.ThrowIf(_key is null, this);
                return _key;
            }
        }

        public void Dispose()
        {
            if (_key is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
    }
}
