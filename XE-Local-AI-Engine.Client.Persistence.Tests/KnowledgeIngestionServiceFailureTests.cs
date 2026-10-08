namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Ingestion state-machine failure path on a real database: when the embedding model cannot be resolved, the run must
///     end with <c>status = Failed</c> and a fixed, content-free <c>failure_reason</c> — never chunk or document text. The
///     embedder is driven with a provider resolver that throws the caught <see cref="InvalidOperationException" />, which
///     the real embedder maps to a content-free <see cref="KnowledgeIngestionException" />. The seeded document text is a
///     distinctive token that must NOT leak into the persisted failure reason.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class KnowledgeIngestionServiceFailureTests : IDisposable
{
    private const string SecretDocumentText = "classifiedpayload42";

    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }

        _keyHolder.Dispose();
    }

    [Test]
    public async Task RunAsync_WhenNoEmbeddingModelIsAvailable_MarksTheDocumentFailedWithAContentFreeReason()
    {
        var databasePath = GetDatabasePath("ingestion-failure.sqlite");
        var documentId = Guid.NewGuid();

        await MigrateAsync(databasePath);
        await SeedPendingDocumentAsync(databasePath, documentId);

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            var service = CreateService(context);
            await service.RunAsync(documentId, CancellationToken.None);
        }

        var (status, failureReason) = await ReadStatusAsync(databasePath, documentId);
        AssertEx.Equal(KnowledgeDocumentStatus.Failed.ToString(), status);
        var reason = AssertEx.NotNull(failureReason, "A failed ingestion should persist a failure reason.");
        AssertEx.True(reason.Contains("embedding model", StringComparison.OrdinalIgnoreCase),
            "The failure reason should describe the embedding-unavailable failure category.");
        AssertEx.False(reason.Contains(SecretDocumentText, StringComparison.Ordinal),
            "The persisted failure reason must never contain document text.");
    }

    [Test]
    public async Task IngestionRerun_FailingEmbedder_DoesNotFlipIndexedToFailed()
    {
        // A duplicate run of an already indexed document used to drop it out of search and, when the run failed, mark it Failed.
        var databasePath = GetDatabasePath("ingestion-indexed-rerun.sqlite");
        var documentId = Guid.NewGuid();
        await MigrateAsync(databasePath);
        await SeedPendingDocumentAsync(databasePath, documentId, KnowledgeDocumentStatus.Indexed);

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await CreateService(context).RunAsync(documentId, CancellationToken.None);
        }

        var (status, failureReason) = await ReadStatusAsync(databasePath, documentId);
        AssertEx.Equal(KnowledgeDocumentStatus.Indexed.ToString(), status);
        AssertEx.Null(failureReason);
    }

    [Test]
    public async Task RunAsync_WhenExtractionOutlivesTheDocumentBudget_MarksTheDocumentFailedAndReturns()
    {
        // The extractor never completes and ignores its token, like a looping parser: only the budget can end the run.
        var databasePath = GetDatabasePath("ingestion-budget.sqlite");
        var documentId = Guid.NewGuid();
        await MigrateAsync(databasePath);
        await SeedPendingDocumentAsync(databasePath, documentId);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<DocumentStructuredExtractionResult>();
        var extractor = Substitute.For<IDocumentTextExtractor>();
        extractor.ExtractStructuredAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(_ =>
                 {
                     entered.TrySetResult();
                     return never.Task;
                 });
        var clock = new ManualDeadlineTimeProvider();

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            var run = CreateService(context, extractor, clock).RunAsync(documentId, CancellationToken.None);
            await entered.Task;
            clock.FireDeadlines();
            await run.WaitAsync(TimeSpan.FromSeconds(30));
        }

        var (status, failureReason) = await ReadStatusAsync(databasePath, documentId);
        AssertEx.Equal(KnowledgeDocumentStatus.Failed.ToString(), status);
        AssertEx.True(AssertEx.NotNull(failureReason).Contains("took too long", StringComparison.Ordinal), failureReason);
    }

    [Test]
    public async Task RunAsync_WhenIngestionStarts_CountsOneAttempt()
    {
        var databasePath = GetDatabasePath("ingestion-attempt-count.sqlite");
        var documentId = Guid.NewGuid();
        await MigrateAsync(databasePath);
        await SeedPendingDocumentAsync(databasePath, documentId);

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await CreateService(context).RunAsync(documentId, CancellationToken.None);
        }

        AssertEx.Equal(1L, await ReadAttemptsAsync(databasePath, documentId), "The move to Extracting must count one ingestion attempt.");
    }

    [Test]
    public async Task RunAsync_WhenTheWorkerStopsMidRun_ReturnsTheDocumentToPendingWithoutCountingTheAttempt()
    {
        var databasePath = GetDatabasePath("ingestion-clean-drain.sqlite");
        var documentId = Guid.NewGuid();
        await MigrateAsync(databasePath);
        await SeedPendingDocumentAsync(databasePath, documentId);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<DocumentStructuredExtractionResult>();
        var extractor = Substitute.For<IDocumentTextExtractor>();
        extractor.ExtractStructuredAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(_ =>
                 {
                     entered.TrySetResult();
                     return never.Task;
                 });
        using var workerStop = new CancellationTokenSource();

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            var run = CreateService(context, extractor).RunAsync(documentId, workerStop.Token);
            await entered.Task;
            AssertEx.Equal(1L, await ReadAttemptsAsync(databasePath, documentId), "The run must have counted its start before the stop.");
            await workerStop.CancelAsync();
            _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(30)));
        }

        var (status, failureReason) = await ReadStatusAsync(databasePath, documentId);
        AssertEx.Equal(KnowledgeDocumentStatus.Pending.ToString(), status);
        AssertEx.Null(failureReason);
        AssertEx.Equal(0L, await ReadAttemptsAsync(databasePath, documentId), "A clean worker stop must not burn an attempt.");
    }

    [Test]
    public async Task ResetNonTerminalToPendingAsync_SplitsRequeuedFromRepeatedlyInterruptedDocuments()
    {
        var databasePath = GetDatabasePath("recovery-reset.sqlite");
        await MigrateAsync(databasePath);
        var fresh = Guid.NewGuid();
        var retrying = Guid.NewGuid();
        var exhausted = Guid.NewGuid();
        var indexed = Guid.NewGuid();
        var failed = Guid.NewGuid();
        await SeedPendingDocumentAsync(databasePath, fresh);
        await SeedPendingDocumentAsync(databasePath, retrying, KnowledgeDocumentStatus.Chunking, ingestionAttempts: 2);
        await SeedPendingDocumentAsync(databasePath, exhausted, KnowledgeDocumentStatus.Embedding, ingestionAttempts: 3);
        await SeedPendingDocumentAsync(databasePath, indexed, KnowledgeDocumentStatus.Indexed, ingestionAttempts: 5);
        await SeedPendingDocumentAsync(databasePath, failed, KnowledgeDocumentStatus.Failed, ingestionAttempts: 4);

        IReadOnlyList<Guid> requeued;
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            requeued = await CreateCatalog(context).ResetNonTerminalToPendingAsync(CancellationToken.None);
        }

        AssertEx.Equal(2, requeued.Count, "Only the fresh and the still-retrying documents are re-queued.");
        AssertEx.True(requeued.Contains(fresh) && requeued.Contains(retrying), "Both documents under the limit must be re-queued.");
        var (retryingStatus, retryingReason) = await ReadStatusAsync(databasePath, retrying);
        AssertEx.Equal(KnowledgeDocumentStatus.Pending.ToString(), retryingStatus);
        AssertEx.Null(retryingReason);
        var (exhaustedStatus, exhaustedReason) = await ReadStatusAsync(databasePath, exhausted);
        AssertEx.Equal(KnowledgeDocumentStatus.Failed.ToString(), exhaustedStatus);
        AssertEx.Equal("Ingestion was interrupted repeatedly and was stopped.", exhaustedReason);
        AssertEx.Equal(KnowledgeDocumentStatus.Indexed.ToString(), (await ReadStatusAsync(databasePath, indexed)).Status);
        AssertEx.Equal(KnowledgeDocumentStatus.Failed.ToString(), (await ReadStatusAsync(databasePath, failed)).Status);
    }

    [Test]
    public async Task StartupRecovery_AfterThreeInterruptedStarts_FailsTheDocumentAndStopsRequeuingIt()
    {
        var databasePath = GetDatabasePath("recovery-third-boot.sqlite");
        var documentId = Guid.NewGuid();
        await MigrateAsync(databasePath);
        await SeedPendingDocumentAsync(databasePath, documentId);

        // Each boot re-queues the document and its run starts, then the process dies mid-pipeline.
        for (var boot = 1; boot <= 3; boot++)
        {
            AssertEx.True((await ResetNonTerminalAsync(databasePath)).Contains(documentId), $"Boot {boot} must still re-queue the document.");
            await SimulateCrashedStartAsync(databasePath, documentId);
        }

        AssertEx.False((await ResetNonTerminalAsync(databasePath)).Contains(documentId), "The fourth boot must not re-queue it again.");
        var (status, failureReason) = await ReadStatusAsync(databasePath, documentId);
        AssertEx.Equal(KnowledgeDocumentStatus.Failed.ToString(), status);
        AssertEx.Equal("Ingestion was interrupted repeatedly and was stopped.", failureReason);
        AssertEx.False((await ResetNonTerminalAsync(databasePath)).Contains(documentId), "A stopped document stays stopped on later boots.");
    }

    [Test]
    public async Task ResetToPendingAsync_OnAStoppedDocument_ZeroesTheAttemptsSoRecoveryRequeuesItAgain()
    {
        var databasePath = GetDatabasePath("recovery-user-retry.sqlite");
        var documentId = Guid.NewGuid();
        await MigrateAsync(databasePath);
        await SeedPendingDocumentAsync(databasePath, documentId, KnowledgeDocumentStatus.Failed, ingestionAttempts: 3);

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            AssertEx.True(await CreateCatalog(context).ResetToPendingAsync(documentId, CancellationToken.None));
        }

        AssertEx.Equal(0L, await ReadAttemptsAsync(databasePath, documentId), "An explicit retry must start the count again.");
        AssertEx.True((await ResetNonTerminalAsync(databasePath)).Contains(documentId), "A retried document must survive the next boot's recovery.");
    }

    [Test]
    public async Task ResetStaleDocumentsToPendingAsync_ZeroesTheAttempts()
    {
        // The seeded row carries no parser version, so it is stale against the current pipeline without a provider.
        var databasePath = GetDatabasePath("recovery-stale-reset.sqlite");
        var documentId = Guid.NewGuid();
        await MigrateAsync(databasePath);
        await SeedPendingDocumentAsync(databasePath, documentId, KnowledgeDocumentStatus.Indexed, ingestionAttempts: 2);

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            AssertEx.True((await CreateCatalog(context).ResetStaleDocumentsToPendingAsync(CancellationToken.None)).Contains(documentId),
                "The seeded document must be treated as stale.");
        }

        AssertEx.Equal(0L, await ReadAttemptsAsync(databasePath, documentId), "A stale-index reset must start the count again.");
    }

    private async Task<IReadOnlyList<Guid>> ResetNonTerminalAsync(string databasePath)
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        return await CreateCatalog(context).ResetNonTerminalToPendingAsync(CancellationToken.None);
    }

    private static KnowledgeDocumentCatalogService CreateCatalog(NodeChatDbContext context)
    {
        var options = Options.Create(new KnowledgeBaseOptions());
        return new KnowledgeDocumentCatalogService(context,
            new ThrowingProviderResolver(),
            new EmbeddingModelResolver(options),
            options,
            TimeProvider.System);
    }

    // What a run that reached Extracting leaves behind when the process is killed: the counted start, a mid-pipeline status.
    private static async Task SimulateCrashedStartAsync(string databasePath, Guid documentId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE knowledge_documents SET status = 'Embedding', ingestion_attempts = ingestion_attempts + 1 WHERE document_id = $id;";
        command.Parameters.AddWithValue("$id", documentId);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ReadAttemptsAsync(string databasePath, Guid documentId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ingestion_attempts FROM knowledge_documents WHERE document_id = $id;";
        command.Parameters.AddWithValue("$id", documentId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static KnowledgeIngestionService CreateService(NodeChatDbContext context, IDocumentTextExtractor? extractor = null, TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new KnowledgeBaseOptions());

        var blobStore = Substitute.For<IKnowledgeDocumentBlobStore>();
        blobStore.ReadBytesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<byte[]?>(new byte[]
                 {
                     1,
                     2,
                     3
                 }));

        if (extractor is null)
        {
            extractor = Substitute.For<IDocumentTextExtractor>();
            extractor.ExtractStructuredAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult(new DocumentStructuredExtractionResult
                     {
                         Status = DocumentExtractionStatus.Extracted,
                         Document = BuildExtractedDocument(),
                         Error = null
                     }));
        }

        var embedder = new KnowledgeChunkEmbedder(new ThrowingProviderResolver(),
            new EmbeddingModelResolver(options),
            new KnowledgeEmbeddingPrefixer(),
            options);

        return new KnowledgeIngestionService(context,
            blobStore,
            extractor,
            new HeaderBoundaryChunkingService(options),
            embedder,
            Substitute.For<IKnowledgeIndexWriter>(),
            Substitute.For<IKnowledgeIndexingNotifier>(),
            timeProvider ?? TimeProvider.System,
            NullLogger<KnowledgeIngestionService>.Instance,
            options: options);
    }

    private static IngestionDocument BuildExtractedDocument()
    {
        var document = new IngestionDocument("test-document");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentHeader("Heading")
        {
            Text = "Heading",
            Level = 1
        });
        section.Elements.Add(new IngestionDocumentParagraph(SecretDocumentText)
        {
            Text = SecretDocumentText
        });
        document.Sections.Add(section);
        return document;
    }

    // A copy of the shared at-head template, not a replay of the whole declared chain: this suite exercises a service
    // over the schema, never the migrator that produced it. See MigratedDatabaseTemplate.
    private static async Task MigrateAsync(string databasePath)
    {
        await MigratedDatabaseTemplate.CopyChatHeadAsync(databasePath);
    }

    private static async Task SeedPendingDocumentAsync(string databasePath,
        Guid documentId,
        KnowledgeDocumentStatus status = KnowledgeDocumentStatus.Pending,
        int ingestionAttempts = 0)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO knowledge_documents (document_id, original_file_name, mime_type, extension, size_bytes, content_hash, storage_path, status, chunk_count, embedding_model, ingestion_attempts, created_at_utc, updated_at_utc)
            VALUES ($id, $name, 'text/plain', '.txt', 10, $hash, $path, $status, 0, 'nomic-embed-text', $attempts, 1, 1);
            """;
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$attempts", ingestionAttempts);
        command.Parameters.AddWithValue("$name", new byte[]
        {
            1,
            2,
            3
        });
        command.Parameters.AddWithValue("$hash", "hash-" + documentId.ToString("N"));
        command.Parameters.AddWithValue("$path", documentId.ToString("D") + ".txt");
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<(string Status, string? FailureReason)> ReadStatusAsync(string databasePath, Guid documentId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, failure_reason FROM knowledge_documents WHERE document_id = $id;";
        command.Parameters.AddWithValue("$id", documentId);
        await using var reader = await command.ExecuteReaderAsync();
        _ = await reader.ReadAsync();
        var failureReason = await reader.IsDBNullAsync(1) ? null : reader.GetString(1);
        return (reader.GetString(0), failureReason);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    /// <summary>Hands out timers that fire only when the test says so, so a deadline elapses without waiting for it.</summary>
    private sealed class ManualDeadlineTimeProvider : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<Action> _timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                _timers.Add(() => callback(state));
            }

            return new InertTimer();
        }

        public void FireDeadlines()
        {
            List<Action> timers;
            lock (_gate)
            {
                timers = [.. _timers];
            }

            foreach (var fire in timers)
            {
                fire();
            }
        }

        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) =>
                true;

            public void Dispose()
            {
                // Nothing scheduled to stop.
            }

            public ValueTask DisposeAsync() =>
                ValueTask.CompletedTask;
        }
    }

    /// <summary>A provider resolver that always fails to resolve, standing in for "no embedding model available".</summary>
    private sealed class ThrowingProviderResolver : ILocalModelProviderResolver
    {
        public int MaxLoadedProcesses => 1;

        public ILocalModelProvider DefaultProvider => throw new InvalidOperationException("No provider is registered.");

        public Task<string> ResolveProviderNameForModelAsync(string modelName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult("llamacpp");
        }

        public ILocalModelProvider ResolveProvider(string providerName)
        {
            throw new InvalidOperationException("The embedding provider is not registered.");
        }

        public Task<ILocalModelProvider> ResolveProviderForModelAsync(string modelName, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The embedding provider is not registered.");
        }

        public void InvalidateModelProviderMap()
        {
            // No cache in this test double.
        }
    }
}
