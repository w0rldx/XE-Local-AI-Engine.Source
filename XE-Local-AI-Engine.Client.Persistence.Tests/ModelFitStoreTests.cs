namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class ModelFitStoreTests : IDisposable
{
    private const string ImageId = "llmfit-recommender-0-9-30";

    private const string ImageReference =
        "ghcr.io/alexsjones/llmfit:0.9.30@sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c";

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // ApprovedUtilityImageStore — upsert / list / get / enable toggle / seed preserve
    // -------------------------------------------------------------------------

    [Test]
    public async Task ApprovedImage_UpsertSeed_ThenGetAndList_RoundTrips()
    {
        var databasePath = GetDatabasePath("image-seed-roundtrip.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ApprovedUtilityImageStore(context, TimeProvider.System);
        var seeded = await store.UpsertSeedAsync(CreateImageRecord(false));

        AssertEx.Equal(ImageId, seeded.ApprovedImageId);
        AssertEx.Equal(ImageReference, seeded.ImageReference);
        AssertEx.False(seeded.Enabled, "Seed should ship disabled.");
        AssertEx.True(seeded.CreatedAtUtc > 0, "Seed should stamp CreatedAtUtc.");

        var byId = AssertEx.NotNull(await store.GetByIdAsync(ImageId));
        AssertEx.Equal(ImageReference, byId.ImageReference);
        AssertEx.Equal(UtilityImagePurpose.ModelRecommendation | UtilityImagePurpose.ModelBenchmark, byId.Purpose);

        var all = await store.ListAsync();
        AssertEx.Equal(expected: 1, all.Count);
    }

    [Test]
    public async Task ApprovedImage_GetById_IsCaseInsensitive()
    {
        var databasePath = GetDatabasePath("image-nocase.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ApprovedUtilityImageStore(context, TimeProvider.System);
        _ = await store.UpsertSeedAsync(CreateImageRecord(false));

        var byUpperId = await store.GetByIdAsync(ImageId.ToUpperInvariant());

        AssertEx.NotNull(byUpperId, "The NOCASE PK should match an upper-cased id.");
    }

    [Test]
    public async Task ApprovedImage_SetEnabled_TogglesAndPersists()
    {
        var databasePath = GetDatabasePath("image-set-enabled.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ApprovedUtilityImageStore(context, TimeProvider.System);
        _ = await store.UpsertSeedAsync(CreateImageRecord(false));

        var enabled = AssertEx.NotNull(await store.SetEnabledAsync(ImageId, enabled: true));
        AssertEx.True(enabled.Enabled, "SetEnabled(true) should enable the descriptor.");

        var reread = AssertEx.NotNull(await store.GetByIdAsync(ImageId));
        AssertEx.True(reread.Enabled, "The enabled toggle should persist.");
    }

    [Test]
    public async Task ApprovedImage_SetEnabled_WhenIdUnknown_ReturnsNull()
    {
        var databasePath = GetDatabasePath("image-set-enabled-unknown.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ApprovedUtilityImageStore(context, TimeProvider.System);

        AssertEx.Null(await store.SetEnabledAsync("does-not-exist", enabled: true));
    }

    [Test]
    public async Task ApprovedImage_UpsertSeed_PreservesOperatorEnabledToggle()
    {
        var databasePath = GetDatabasePath("image-seed-preserve-enabled.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ApprovedUtilityImageStore(context, TimeProvider.System);

        // Seed disabled, then the operator enables it.
        _ = await store.UpsertSeedAsync(CreateImageRecord(false));
        _ = await store.SetEnabledAsync(ImageId, enabled: true);

        // Re-seed (as on the next startup) with code-owned changes and Enabled=false in the descriptor.
        var reseeded = await store.UpsertSeedAsync(CreateImageRecord(false) with
        {
            DisplayName = "llmfit recommender (updated)"
        });

        AssertEx.True(reseeded.Enabled, "Re-seed must preserve the operator-set Enabled toggle.");
        AssertEx.Equal("llmfit recommender (updated)", reseeded.DisplayName);
    }

    [Test]
    public async Task ApprovedImage_TouchUsed_StampsUsageTimestamps()
    {
        var databasePath = GetDatabasePath("image-touch-used.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ApprovedUtilityImageStore(context, TimeProvider.System);
        _ = await store.UpsertSeedAsync(CreateImageRecord(true));

        var touched = AssertEx.NotNull(await store.TouchUsedAsync(ImageId, lastUsedAtUtc: 5_000, lastSuccessfulRunAtUtc: 5_500));

        AssertEx.Equal(expected: 5_000L, touched.LastUsedAtUtc);
        AssertEx.Equal(expected: 5_500L, touched.LastSuccessfulRunAtUtc);
    }

    // -------------------------------------------------------------------------
    // ModelFitSnapshotStore — create / mark terminal / latest-successful
    // -------------------------------------------------------------------------

    [Test]
    public async Task Snapshot_CreateRunning_ThenMarkSucceeded_SetsLatestSuccessful()
    {
        var databasePath = GetDatabasePath("snapshot-mark-succeeded.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ModelFitSnapshotStore(context, TimeProvider.System);
        var created = await store.CreateRunningAsync(CreateRecommendInput("coding"));

        AssertEx.Equal(ModelFitRunStatus.Queued, created.Status);
        AssertEx.False(created.IsLatestSuccessful, "A queued run is not latest-successful.");

        var succeeded = AssertEx.NotNull(await store.MarkTerminalAsync(created.Id, ModelFitRunStatus.Succeeded, exitCode: 0, durationMs: 1_234,
            rawJson: """{"models":[]}""", stderrExcerpt: null, diagnosticsJson: null, completedAtUtc: 9_000));

        AssertEx.Equal(ModelFitRunStatus.Succeeded, succeeded.Status);
        AssertEx.True(succeeded.IsLatestSuccessful, "A succeeded run becomes latest-successful for its key.");

        var latest = AssertEx.NotNull(await store.GetLatestSuccessfulSummaryAsync(ModelFitOperation.Recommend, "coding", "ollama", modelName: null));
        AssertEx.Equal(succeeded.Id, latest.Id);
    }

    [Test]
    public async Task Snapshot_MarkSucceeded_ClearsPriorLatestForSameKey()
    {
        var databasePath = GetDatabasePath("snapshot-latest-replacement.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ModelFitSnapshotStore(context, TimeProvider.System);

        var first = await store.CreateRunningAsync(CreateRecommendInput("coding"));
        _ = await store.MarkTerminalAsync(first.Id, ModelFitRunStatus.Succeeded, exitCode: 0, durationMs: 100, rawJson: null, stderrExcerpt: null, diagnosticsJson: null, completedAtUtc: 1_000);

        var second = await store.CreateRunningAsync(CreateRecommendInput("coding"));
        _ = await store.MarkTerminalAsync(second.Id, ModelFitRunStatus.Succeeded, exitCode: 0, durationMs: 100, rawJson: null, stderrExcerpt: null, diagnosticsJson: null, completedAtUtc: 2_000);

        // The new success demotes the prior one for the same key.
        var firstReread = await store.GetRawByIdAsync(first.Id);
        AssertEx.NotNull(firstReread);

        var latest = AssertEx.NotNull(await store.GetLatestSuccessfulSummaryAsync(ModelFitOperation.Recommend, "coding", "ollama", modelName: null));
        AssertEx.Equal(second.Id, latest.Id);

        // Exactly one row is latest for the key.
        var rawLatestCount = await CountLatestSuccessfulAsync(databasePath, ModelFitOperation.Recommend, "coding", "ollama", modelName: null);
        AssertEx.Equal(expected: 1, rawLatestCount);
    }

    [Test]
    public async Task Snapshot_MarkSucceeded_DifferentKeyKeepsItsOwnLatest()
    {
        var databasePath = GetDatabasePath("snapshot-latest-different-key.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ModelFitSnapshotStore(context, TimeProvider.System);

        var coding = await store.CreateRunningAsync(CreateRecommendInput("coding"));
        _ = await store.MarkTerminalAsync(coding.Id, ModelFitRunStatus.Succeeded, exitCode: 0, durationMs: 100, rawJson: null, stderrExcerpt: null, diagnosticsJson: null, completedAtUtc: 1_000);

        var reasoning = await store.CreateRunningAsync(CreateRecommendInput("reasoning"));
        _ = await store.MarkTerminalAsync(reasoning.Id, ModelFitRunStatus.Succeeded, exitCode: 0, durationMs: 100, rawJson: null, stderrExcerpt: null, diagnosticsJson: null, completedAtUtc: 2_000);

        // Each key keeps its own latest.
        var latestCoding = AssertEx.NotNull(await store.GetLatestSuccessfulSummaryAsync(ModelFitOperation.Recommend, "coding", "ollama", modelName: null));
        var latestReasoning = AssertEx.NotNull(await store.GetLatestSuccessfulSummaryAsync(ModelFitOperation.Recommend, "reasoning", "ollama", modelName: null));

        AssertEx.Equal(coding.Id, latestCoding.Id);
        AssertEx.Equal(reasoning.Id, latestReasoning.Id);
    }

    [Test]
    public async Task Snapshot_TwoSuccessiveSucceededMarks_LeaveExactlyOneLatest()
    {
        var databasePath = GetDatabasePath("snapshot-concurrency-intent.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ModelFitSnapshotStore(context, TimeProvider.System);

        var a = await store.CreateRunningAsync(CreateRecommendInput("chat"));
        var b = await store.CreateRunningAsync(CreateRecommendInput("chat"));

        _ = await store.MarkTerminalAsync(a.Id, ModelFitRunStatus.Succeeded, exitCode: 0, durationMs: 100, rawJson: null, stderrExcerpt: null, diagnosticsJson: null, completedAtUtc: 1_000);
        _ = await store.MarkTerminalAsync(b.Id, ModelFitRunStatus.Succeeded, exitCode: 0, durationMs: 100, rawJson: null, stderrExcerpt: null, diagnosticsJson: null, completedAtUtc: 2_000);

        var latestCount = await CountLatestSuccessfulAsync(databasePath, ModelFitOperation.Recommend, "chat", "ollama", modelName: null);
        AssertEx.Equal(expected: 1, latestCount);
    }

    [Test]
    public async Task Snapshot_MarkFailed_DoesNotSetLatestSuccessful()
    {
        var databasePath = GetDatabasePath("snapshot-mark-failed.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ModelFitSnapshotStore(context, TimeProvider.System);
        var created = await store.CreateRunningAsync(CreateRecommendInput("coding"));

        var failed = AssertEx.NotNull(await store.MarkTerminalAsync(created.Id, ModelFitRunStatus.Failed, exitCode: 1, durationMs: 10,
            rawJson: null, "Error: boom", diagnosticsJson: null, completedAtUtc: 1_000));

        AssertEx.Equal(ModelFitRunStatus.Failed, failed.Status);
        AssertEx.False(failed.IsLatestSuccessful, "A failed run is never latest-successful.");
        AssertEx.Null(await store.GetLatestSuccessfulSummaryAsync(ModelFitOperation.Recommend, "coding", "ollama", modelName: null));
    }

    [Test]
    public async Task Snapshot_MarkTerminal_WhenIdUnknown_ReturnsNull()
    {
        var databasePath = GetDatabasePath("snapshot-mark-unknown.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ModelFitSnapshotStore(context, TimeProvider.System);

        AssertEx.Null(await store.MarkTerminalAsync(Guid.NewGuid(), ModelFitRunStatus.Failed, exitCode: 1, durationMs: 1, rawJson: null, stderrExcerpt: null, diagnosticsJson: null,
            completedAtUtc: 1));
        AssertEx.Null(await store.MarkTerminalAsync(Guid.NewGuid(), ModelFitRunStatus.Succeeded, exitCode: 0, durationMs: 1, rawJson: null, stderrExcerpt: null, diagnosticsJson: null,
            completedAtUtc: 1));
    }

    [Test]
    public async Task Snapshot_ListRecentSummaries_OrdersNewestFirstAndOmitsRawColumns()
    {
        var databasePath = GetDatabasePath("snapshot-list-recent.sqlite");
        using var keyHolder = CreateKeyHolder();
        var clock = new MutableTimeProvider(1_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ModelFitSnapshotStore(context, clock);

        var first = await store.CreateRunningAsync(CreateRecommendInput("coding"));
        clock.Advance(1_000);
        var second = await store.CreateRunningAsync(CreateRecommendInput("coding"));

        var recent = await store.ListRecentSummariesAsync(ModelFitOperation.Recommend, "ollama");

        AssertEx.Equal(expected: 2, recent.Count);
        AssertEx.Equal(second.Id, recent[0].Id);
        AssertEx.Equal(first.Id, recent[1].Id);
        // The summary record has no raw/stderr/diagnostics members at all — the sanitized boundary is in the type shape.
    }

    // -------------------------------------------------------------------------
    // ModelFitSnapshot encrypted columns — decrypt + ciphertext-at-rest
    // -------------------------------------------------------------------------

    [Test]
    public async Task Snapshot_RawColumns_DecryptOnOperatorRead()
    {
        var databasePath = GetDatabasePath("snapshot-raw-decrypt.sqlite");
        using var keyHolder = CreateKeyHolder();
        const string rawJson = """{"models":[{"name":"qwen"}]}""";
        const string stderr = "warmup ok";
        const string diagnostics = """{"system":{"cpu_cores":8}}""";

        Guid snapshotId;
        await using (var writeContext = CreateContext(databasePath, keyHolder))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();

            var store = new ModelFitSnapshotStore(writeContext, TimeProvider.System);
            var created = await store.CreateRunningAsync(CreateRecommendInput("coding"));
            _ = await store.MarkTerminalAsync(created.Id, ModelFitRunStatus.Succeeded, exitCode: 0, durationMs: 100, rawJson, stderr, diagnostics, completedAtUtc: 1_000);
            snapshotId = created.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new ModelFitSnapshotStore(readContext, TimeProvider.System);

        var raw = AssertEx.NotNull(await readStore.GetRawByIdAsync(snapshotId));
        AssertEx.Equal(rawJson, raw.RawJson);
        AssertEx.Equal(stderr, raw.StderrExcerpt);
        AssertEx.Equal(diagnostics, raw.DiagnosticsJson);
    }

    [Test]
    public async Task Snapshot_RawColumns_StoredAsCiphertext()
    {
        var databasePath = GetDatabasePath("snapshot-raw-ciphertext.sqlite");
        using var keyHolder = CreateKeyHolder();
        var rawJson = "SECRET-RAW-" + Guid.NewGuid().ToString("N");

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();

            var store = new ModelFitSnapshotStore(context, TimeProvider.System);
            var created = await store.CreateRunningAsync(CreateRecommendInput("coding"));
            _ = await store.MarkTerminalAsync(created.Id, ModelFitRunStatus.Succeeded, exitCode: 0, durationMs: 100, rawJson, stderrExcerpt: null, diagnosticsJson: null, completedAtUtc: 1_000);
        }

        var rawBytes = await ReadRawSnapshotRawJsonAsync(databasePath);
        var plaintextBytes = Encoding.UTF8.GetBytes(rawJson);

        AssertEx.True(rawBytes.Length > 0, "Encrypted column should have non-empty BLOB data.");
        AssertEx.False(rawBytes.AsSpan().SequenceEqual(plaintextBytes),
            "raw_json column should be encrypted at rest, not plaintext.");
    }

    // -------------------------------------------------------------------------
    // ModelFitRecommendationStore — replace round-trip
    // -------------------------------------------------------------------------

    [Test]
    public async Task Recommendation_ReplaceForSnapshot_RoundTripsOrderedByRank()
    {
        var databasePath = GetDatabasePath("recommendation-replace.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var snapshotStore = new ModelFitSnapshotStore(context, TimeProvider.System);
        var recommendationStore = new ModelFitRecommendationStore(context);

        var snapshot = await snapshotStore.CreateRunningAsync(CreateRecommendInput("coding"));

        var inserted = await recommendationStore.ReplaceForSnapshotAsync(snapshot.Id,
        [
            CreateRecommendation(rank: 2, "model-b", score: 80.0),
            CreateRecommendation(rank: 1, "model-a", score: 95.0)
        ]);

        AssertEx.Equal(expected: 2, inserted);

        var rows = await recommendationStore.ListForSnapshotAsync(snapshot.Id);
        AssertEx.Equal(expected: 2, rows.Count);
        AssertEx.Equal("model-a", rows[0].ModelName);
        AssertEx.Equal(expected: 95.0, rows[0].Score);
        AssertEx.Equal(expected: 1, rows[0].Rank);
        AssertEx.Equal("model-b", rows[1].ModelName);
    }

    [Test]
    public async Task Recommendation_ReplaceForSnapshot_OverwritesPreviousRows()
    {
        var databasePath = GetDatabasePath("recommendation-overwrite.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var snapshotStore = new ModelFitSnapshotStore(context, TimeProvider.System);
        var recommendationStore = new ModelFitRecommendationStore(context);

        var snapshot = await snapshotStore.CreateRunningAsync(CreateRecommendInput("coding"));

        _ = await recommendationStore.ReplaceForSnapshotAsync(snapshot.Id,
        [
            CreateRecommendation(rank: 1, "old-model", score: 50.0)
        ]);

        _ = await recommendationStore.ReplaceForSnapshotAsync(snapshot.Id,
        [
            CreateRecommendation(rank: 1, "new-model", score: 90.0)
        ]);

        var rows = await recommendationStore.ListForSnapshotAsync(snapshot.Id);
        AssertEx.Equal(expected: 1, rows.Count);
        AssertEx.Equal("new-model", rows[0].ModelName);
    }

    // -------------------------------------------------------------------------
    // ModelFitBenchmarkStore — replace round-trip + encryption
    // -------------------------------------------------------------------------

    [Test]
    public async Task Benchmark_ReplaceForSnapshot_RoundTripsAndDecryptsRaw()
    {
        var databasePath = GetDatabasePath("benchmark-replace.sqlite");
        using var keyHolder = CreateKeyHolder();
        const string rawJson = """{"tps":42.0}""";

        Guid snapshotId;
        await using (var writeContext = CreateContext(databasePath, keyHolder))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();

            var snapshotStore = new ModelFitSnapshotStore(writeContext, TimeProvider.System);
            var benchmarkStore = new ModelFitBenchmarkStore(writeContext);

            var snapshot = await snapshotStore.CreateRunningAsync(CreateBenchmarkInput("qwen"));
            snapshotId = snapshot.Id;

            var inserted = await benchmarkStore.ReplaceForSnapshotAsync(snapshot.Id,
            [
                new ModelFitBenchmarkInput("qwen", "ollama", TokensPerSecond: 42.0, TtftMs: 12.0, TotalLatencyMs: 200.0,
                    Runs: 3, rawJson, DiagnosticsJson: """{"note":"ok"}""")
            ]);

            AssertEx.Equal(expected: 1, inserted);
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new ModelFitBenchmarkStore(readContext);

        var rows = await readStore.ListForSnapshotAsync(snapshotId);
        AssertEx.Equal(expected: 1, rows.Count);
        AssertEx.Equal("qwen", rows[0].ModelName);
        AssertEx.Equal(expected: 42.0, rows[0].TokensPerSecond);
        AssertEx.Equal(rawJson, rows[0].RawJson);
        AssertEx.Equal(expected: """{"note":"ok"}""", rows[0].DiagnosticsJson);
    }

    [Test]
    public async Task Benchmark_RawColumn_StoredAsCiphertext()
    {
        var databasePath = GetDatabasePath("benchmark-ciphertext.sqlite");
        using var keyHolder = CreateKeyHolder();
        var rawJson = "SECRET-BENCH-" + Guid.NewGuid().ToString("N");

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();

            var snapshotStore = new ModelFitSnapshotStore(context, TimeProvider.System);
            var benchmarkStore = new ModelFitBenchmarkStore(context);

            var snapshot = await snapshotStore.CreateRunningAsync(CreateBenchmarkInput("qwen"));
            _ = await benchmarkStore.ReplaceForSnapshotAsync(snapshot.Id,
            [
                new ModelFitBenchmarkInput("qwen", "ollama", TokensPerSecond: 1.0, TtftMs: 1.0, TotalLatencyMs: 1.0, Runs: 1, rawJson, DiagnosticsJson: null)
            ]);
        }

        var rawBytes = await ReadRawBenchmarkRawJsonAsync(databasePath);
        var plaintextBytes = Encoding.UTF8.GetBytes(rawJson);

        AssertEx.True(rawBytes.Length > 0, "Encrypted column should have non-empty BLOB data.");
        AssertEx.False(rawBytes.AsSpan().SequenceEqual(plaintextBytes),
            "benchmark raw_json column should be encrypted at rest, not plaintext.");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ApprovedUtilityImageRecord CreateImageRecord(bool enabled)
    {
        return new ApprovedUtilityImageRecord(ImageId,
            "llmfit recommender 0.9.30",
            Description: null,
            UtilityImagePurpose.ModelRecommendation | UtilityImagePurpose.ModelBenchmark,
            ImageReference,
            SourceUrl: null,
            "0.9.30",
            enabled,
            DeprecatedAtUtc: null,
            ReplacementApprovedImageId: null,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0,
            LastUsedAtUtc: null,
            LastSuccessfulRunAtUtc: null,
            DiagnosticsJson: """{"license":"MIT"}""");
    }

    private static ModelFitSnapshotInput CreateRecommendInput(string useCase)
    {
        return new ModelFitSnapshotInput(ImageId,
            ModelFitOperation.Recommend,
            useCase,
            "ollama",
            ModelName: null,
            ModelFitRunStatus.Queued,
            StartedAtUtc: null);
    }

    private static ModelFitSnapshotInput CreateBenchmarkInput(string modelName)
    {
        return new ModelFitSnapshotInput(ImageId,
            ModelFitOperation.Benchmark,
            UseCase: null,
            "ollama",
            modelName,
            ModelFitRunStatus.Queued,
            StartedAtUtc: null);
    }

    private static ModelFitRecommendationInput CreateRecommendation(int rank, string modelName, double score)
    {
        return new ModelFitRecommendationInput(rank,
            modelName,
            modelName,
            score,
            "Good",
            "CPU",
            "Q5_K_M",
            EstimatedTokensPerSecond: 20.0,
            RequiredRamMb: 4_096.0,
            RequiredVramMb: null,
            ContextTokens: 8_192,
            IsInstalled: false,
            modelName,
            DiagnosticsJson: null);
    }

    private static async Task<int> CountLatestSuccessfulAsync(string databasePath,
        ModelFitOperation operation,
        string? useCase,
        string providerName,
        string? modelName)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM model_fit_snapshots WHERE is_latest_successful = 1 AND operation = $op " +
            "AND ((use_case IS NULL AND $uc IS NULL) OR use_case = $uc) AND provider_name = $pn " +
            "AND ((model_name IS NULL AND $mn IS NULL) OR model_name = $mn);";
        _ = command.Parameters.AddWithValue("$op", (int)operation);
        _ = command.Parameters.AddWithValue("$uc", (object?)useCase ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$pn", providerName);
        _ = command.Parameters.AddWithValue("$mn", (object?)modelName ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value);
    }

    private static async Task<byte[]> ReadRawSnapshotRawJsonAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT raw_json FROM model_fit_snapshots WHERE raw_json IS NOT NULL LIMIT 1;";
        var value = await command.ExecuteScalarAsync();
        return value as byte[] ?? throw new AssertionException("Expected a non-null encrypted BLOB in model_fit_snapshots.raw_json.");
    }

    private static async Task<byte[]> ReadRawBenchmarkRawJsonAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT raw_json FROM model_fit_benchmarks WHERE raw_json IS NOT NULL LIMIT 1;";
        var value = await command.ExecuteScalarAsync();
        return value as byte[] ?? throw new AssertionException("Expected a non-null encrypted BLOB in model_fit_benchmarks.raw_json.");
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

    private sealed class MutableTimeProvider(long initialMilliseconds) : TimeProvider
    {
        private long _milliseconds = initialMilliseconds;

        public void Advance(long milliseconds)
        {
            _milliseconds += milliseconds;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(_milliseconds);
        }
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
