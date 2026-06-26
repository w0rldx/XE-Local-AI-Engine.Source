namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class InferenceProfileStoreTests : IDisposable
{
    private const string MachineKey = "machine-7f3a";
    private const string ModelName = "qwen3:Q4_K_M";
    private const int ChatRole = 0;
    private const string Backend = "cuda";

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task Profile_CreateExplored_ThenReExploreAtNewCtx_OverwritesSingleConfig()
    {
        var databasePath = GetDatabasePath("profile-reexplore-overwrite.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new InferenceProfileStore(context, TimeProvider.System);

        var first = await store.CreateOrUpdateExploredAsync(CreateInput(ctxSize: 4_096));
        AssertEx.Equal(InferenceProfileStatus.Explored, first.Status);
        AssertEx.Equal(expected: 4_096, first.CtxSize);

        var second = await store.CreateOrUpdateExploredAsync(CreateInput(ctxSize: 8_192));

        // Latest explore wins for the single live config: same row id, overwritten ctx.
        AssertEx.Equal(first.Id, second.Id);
        AssertEx.Equal(expected: 8_192, second.CtxSize);

        var byKey = AssertEx.NotNull(await store.GetByKeyAsync(MachineKey, ModelName, ChatRole, Backend));
        AssertEx.Equal(expected: 8_192, byKey.CtxSize);

        var all = await store.ListAsync();
        AssertEx.Equal(expected: 1, all.Count);
    }

    [Test]
    public async Task Profile_MarkFrozen_TransitionsExploredToFrozen()
    {
        var databasePath = GetDatabasePath("profile-mark-frozen.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new InferenceProfileStore(context, TimeProvider.System);
        var explored = await store.CreateOrUpdateExploredAsync(CreateInput(ctxSize: 4_096));
        AssertEx.Equal(InferenceProfileStatus.Explored, explored.Status);

        var benchmarkSnapshotId = Guid.NewGuid();
        var frozen = AssertEx.NotNull(await store.MarkFrozenAsync(explored.Id, benchmarkSnapshotId, freeVramAtFreezeBytes: 6_200_000_000));

        AssertEx.Equal(InferenceProfileStatus.Frozen, frozen.Status);
        AssertEx.Equal(benchmarkSnapshotId, frozen.BenchmarkSnapshotId);
        AssertEx.Equal(expected: 6_200_000_000L, frozen.FreeVramAtFreezeBytes);

        // The freeze gate only promotes an Explored row — a second freeze of an already-frozen row is rejected.
        AssertEx.Null(await store.MarkFrozenAsync(explored.Id, Guid.NewGuid(), freeVramAtFreezeBytes: 1));

        // Freezing an unknown id returns null.
        AssertEx.Null(await store.MarkFrozenAsync(Guid.NewGuid(), Guid.NewGuid(), freeVramAtFreezeBytes: null));
    }

    [Test]
    public async Task Profile_MarkStale_SetsStaleStatus()
    {
        var databasePath = GetDatabasePath("profile-mark-stale.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new InferenceProfileStore(context, TimeProvider.System);
        var explored = await store.CreateOrUpdateExploredAsync(CreateInput(ctxSize: 4_096));
        _ = await store.MarkFrozenAsync(explored.Id, Guid.NewGuid(), freeVramAtFreezeBytes: 5_000_000_000);

        var stale = AssertEx.NotNull(await store.MarkStaleAsync(explored.Id));
        AssertEx.Equal(InferenceProfileStatus.Stale, stale.Status);

        var byKey = AssertEx.NotNull(await store.GetByKeyAsync(MachineKey, ModelName, ChatRole, Backend));
        AssertEx.Equal(InferenceProfileStatus.Stale, byKey.Status);

        AssertEx.Null(await store.MarkStaleAsync(Guid.NewGuid()));
    }

    [Test]
    public async Task Benchmark_PersistsAllNewMetricAndReproCols_RoundTrip()
    {
        var databasePath = GetDatabasePath("benchmark-new-cols-roundtrip.sqlite");
        using var keyHolder = CreateKeyHolder();

        Guid snapshotId;
        await using (var writeContext = CreateContext(databasePath, keyHolder))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();

            var snapshotStore = new ModelFitSnapshotStore(writeContext, TimeProvider.System);
            var benchmarkStore = new ModelFitBenchmarkStore(writeContext);

            var snapshot = await snapshotStore.CreateRunningAsync(new ModelFitSnapshotInput(
                "llmfit-recommender-0-9-30",
                ModelFitOperation.Benchmark,
                UseCase: null,
                "llama-server",
                ModelName,
                ModelFitRunStatus.Queued,
                StartedAtUtc: null));
            snapshotId = snapshot.Id;

            var inserted = await benchmarkStore.ReplaceForSnapshotAsync(snapshot.Id,
            [
                new ModelFitBenchmarkInput(ModelName, "llama-server", TokensPerSecond: 42.0, TtftMs: 12.0, TotalLatencyMs: 200.0,
                    Runs: 3, RawJson: """{"tps":42.0}""", DiagnosticsJson: null,
                    PpTokensPerSecond: 512.0, CacheHitRate: 0.83, ToolLoopMs: 95.0, VramLoadBytes: 6_100_000_000,
                    VramAfterBytes: 6_050_000_000, LlamacppBuild: "b9692", Quant: "Q4_K_M", CtxSize: 8_192,
                    KvType: "q8_0", Backend: Backend, MachineKey: MachineKey, NGpuLayers: 33,
                    TensorSplit: "0.5,0.5", OverrideTensor: "exps=CPU")
            ]);

            AssertEx.Equal(expected: 1, inserted);
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new ModelFitBenchmarkStore(readContext);

        var rows = await readStore.ListForSnapshotAsync(snapshotId);
        AssertEx.Equal(expected: 1, rows.Count);

        var row = rows[0];
        AssertEx.Equal(expected: 512.0, row.PpTokensPerSecond);
        AssertEx.Equal(expected: 0.83, row.CacheHitRate);
        AssertEx.Equal(expected: 95.0, row.ToolLoopMs);
        AssertEx.Equal(expected: 6_100_000_000L, row.VramLoadBytes);
        AssertEx.Equal(expected: 6_050_000_000L, row.VramAfterBytes);
        AssertEx.Equal("b9692", row.LlamacppBuild);
        AssertEx.Equal("Q4_K_M", row.Quant);
        AssertEx.Equal(expected: 8_192, row.CtxSize);
        AssertEx.Equal("q8_0", row.KvType);
        AssertEx.Equal(Backend, row.Backend);
        AssertEx.Equal(MachineKey, row.MachineKey);
        AssertEx.Equal(expected: 33, row.NGpuLayers);
        AssertEx.Equal("0.5,0.5", row.TensorSplit);
        AssertEx.Equal("exps=CPU", row.OverrideTensor);
    }

    private static InferenceProfileInput CreateInput(int ctxSize)
    {
        return new InferenceProfileInput(MachineKey,
            ModelName,
            ChatRole,
            Backend,
            LlamacppBuild: "b9692",
            Quant: "Q4_K_M",
            ctxSize,
            NGpuLayers: 33,
            TensorSplit: null,
            OverrideTensor: "exps=CPU",
            KvTypeK: null,
            KvTypeV: null,
            FlashAttn: false,
            NParams: 7_600_000_000,
            IsMoe: false,
            ExpertCount: null);
    }

    private static NodeChatDbContext CreateContext(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        return AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private static INodeSqliteKeyHolder CreateKeyHolder()
    {
        var key = Enumerable.Range(start: 0, count: 32).Select(static v => (byte)(v + 1)).ToArray();
        return new FixedNodeSqliteKeyHolder(key);
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
