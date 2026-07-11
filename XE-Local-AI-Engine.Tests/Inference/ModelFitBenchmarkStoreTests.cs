namespace XE_Local_AI_Engine.Tests.Inference;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ModelFitBenchmarkStore.GetLatestSuccessfulForProfileAsync" /> backs the freeze gate's revision binding:
///     it returns the newest benchmark row bound to a profile whose parent snapshot Succeeded, and excludes both legacy
///     rows without a <c>ProfileId</c> and rows whose snapshot did not succeed — so a freeze can never be justified by a
///     benchmark that belongs to a different (or unsuccessful) run. Exercises the real SQLite schema built by
///     <c>EnsureCreated</c>, which includes the additive <c>profile_id</c> column.
/// </summary>
public sealed class ModelFitBenchmarkStoreTests : IDisposable
{
    private const string Provider = "llamacpp";
    private const string Model = "bartowski/Model-GGUF:Q4_K_M";

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task GetLatestSuccessfulForProfile_ReturnsNewestSucceededRowBoundToProfile()
    {
        await using var provider = await BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(100));
        var snapshots = new ModelFitSnapshotStore(dbContext, time);
        var benchmarks = new ModelFitBenchmarkStore(dbContext);
        var profileId = Guid.NewGuid();

        var olderSnapshotId = await CreateBenchmarkAsync(snapshots, benchmarks, time, ModelFitRunStatus.Succeeded, profileId, ctxSize: 8192).ConfigureAwait(false);
        time.Advance(TimeSpan.FromSeconds(1));
        var newerSnapshotId = await CreateBenchmarkAsync(snapshots, benchmarks, time, ModelFitRunStatus.Succeeded, profileId, ctxSize: 4096).ConfigureAwait(false);

        var result = await benchmarks.GetLatestSuccessfulForProfileAsync(profileId, CancellationToken.None).ConfigureAwait(false);

        var row = AssertEx.NotNull(result);
        AssertEx.Equal<Guid?>(profileId, row.ProfileId);
        // The newer run wins; its distinctive ctx proves it is that row and not the older one.
        AssertEx.Equal<int?>(4096, row.CtxSize);
        AssertEx.Equal(newerSnapshotId, row.SnapshotId);
        AssertEx.NotEqual(olderSnapshotId, row.SnapshotId);
    }

    [Test]
    public async Task GetLatestSuccessfulForProfile_IgnoresLegacyRowsWithoutProfileId()
    {
        await using var provider = await BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(100));
        var snapshots = new ModelFitSnapshotStore(dbContext, time);
        var benchmarks = new ModelFitBenchmarkStore(dbContext);

        // A pre-binding (legacy) benchmark row: a successful snapshot but no ProfileId. It must never qualify a freeze.
        _ = await CreateBenchmarkAsync(snapshots, benchmarks, time, ModelFitRunStatus.Succeeded, profileId: null, ctxSize: 8192).ConfigureAwait(false);

        var result = await benchmarks.GetLatestSuccessfulForProfileAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Null(result);
    }

    [Test]
    public async Task GetLatestSuccessfulForProfile_IgnoresNonSucceededSnapshots()
    {
        await using var provider = await BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(100));
        var snapshots = new ModelFitSnapshotStore(dbContext, time);
        var benchmarks = new ModelFitBenchmarkStore(dbContext);
        var profileId = Guid.NewGuid();

        // A benchmark row bound to the profile, but its snapshot Failed — it must not justify a freeze.
        _ = await CreateBenchmarkAsync(snapshots, benchmarks, time, ModelFitRunStatus.Failed, profileId, ctxSize: 8192).ConfigureAwait(false);

        var result = await benchmarks.GetLatestSuccessfulForProfileAsync(profileId, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Null(result);
    }

    private static async Task<Guid> CreateBenchmarkAsync(ModelFitSnapshotStore snapshots,
        ModelFitBenchmarkStore benchmarks,
        FakeTimeProvider time,
        ModelFitRunStatus terminalStatus,
        Guid? profileId,
        int ctxSize)
    {
        var nowMs = time.GetUtcNow().ToUnixTimeMilliseconds();

        var running = await snapshots.CreateRunningAsync(new ModelFitSnapshotInput(ApprovedImageId: Model,
                Operation: ModelFitOperation.Benchmark,
                UseCase: null,
                ProviderName: Provider,
                ModelName: Model,
                Status: ModelFitRunStatus.Running,
                StartedAtUtc: nowMs),
            CancellationToken.None).ConfigureAwait(false);

        _ = await snapshots.MarkTerminalAsync(running.Id,
            terminalStatus,
            exitCode: terminalStatus == ModelFitRunStatus.Succeeded ? 0 : 1,
            durationMs: 1,
            rawJson: null,
            stderrExcerpt: null,
            diagnosticsJson: null,
            completedAtUtc: nowMs,
            CancellationToken.None).ConfigureAwait(false);

        _ = await benchmarks.ReplaceForSnapshotAsync(running.Id,
            new[]
            {
                new ModelFitBenchmarkInput(ModelName: Model,
                    ProviderName: Provider,
                    TokensPerSecond: 42d,
                    TtftMs: null,
                    TotalLatencyMs: null,
                    Runs: 1,
                    RawJson: null,
                    DiagnosticsJson: null,
                    CtxSize: ctxSize,
                    ProfileId: profileId)
            },
            CancellationToken.None).ConfigureAwait(false);

        return running.Id;
    }

    private async Task<ServiceProvider> BuildProviderAsync()
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, "benchmarks.sqlite");

        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));

        var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

        return provider;
    }

    // Local deterministic clock (repo convention: per-test-file nested fake, no external time-testing package).
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _utcNow = start;

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan timeSpan)
        {
            _utcNow = _utcNow.Add(timeSpan);
        }
    }
}
