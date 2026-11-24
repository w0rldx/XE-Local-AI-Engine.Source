namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     An operator-ejected embedder on a real database: the vetoed run parks its row as waiting (never Failed), refunds
///     one attempt, leaves a row it no longer owns alone, and the sweep skips waiting rows until cleared.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class KnowledgeIngestionEjectVetoTests : IDisposable
{
    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly IKnowledgeIndexingNotifier _notifier = Substitute.For<IKnowledgeIndexingNotifier>();

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }

        _keyHolder.Dispose();
    }

    [Test]
    public async Task RunAsync_WhenTheEmbedderIsVetoed_ReleasesTheRowToWaitAndDefers()
    {
        var databasePath = await CreateDatabaseAsync();
        var documentId = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 2);

        var logger = new RecordingLogger<KnowledgeIngestionService>();
        var outcome = await RunAsync(databasePath, documentId, Vetoing(), logger: logger);

        var row = AssertEx.NotNull(await ReadAsync(databasePath, documentId));
        AssertEx.Equal(KnowledgeIngestionRunOutcome.Deferred, outcome, $"The run ended with {row}: {logger.AllText}");
        AssertEx.Equal(nameof(KnowledgeDocumentStatus.Pending), row.Status);
        AssertEx.Equal("embedding-model-ejected", KnowledgeFailureReasonCodes.CodeOf(row.Reason));
        AssertEx.Equal(2L, row.Attempts, "The start counted one attempt and the release must hand exactly that one back.");
        await _notifier.Received(1).NotifyDocumentChangedAsync(documentId, KnowledgeDocumentStatus.Pending, Arg.Any<CancellationToken>());
        await _notifier.DidNotReceive().NotifyDocumentChangedAsync(documentId, KnowledgeDocumentStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("DELETE FROM knowledge_documents WHERE document_id = $id;")]
    [Arguments("UPDATE knowledge_documents SET content_hash = 'replacement', status = 'Pending' WHERE document_id = $id;")]
    [Arguments("UPDATE knowledge_documents SET status = 'Pending', failure_reason = NULL, ingestion_attempts = 0 WHERE document_id = $id;")]
    public async Task RunAsync_WhenTheRowWasDeletedReplacedOrResetMeanwhile_LeavesItAlone(string meanwhile)
    {
        var databasePath = await CreateDatabaseAsync();
        var documentId = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0);
        var before = default(DocumentRow);

        var outcome = await RunAsync(databasePath, documentId, Vetoing(async () =>
        {
            await ExecuteAsync(databasePath, meanwhile, documentId);
            before = await ReadAsync(databasePath, documentId);
        }));

        AssertEx.Equal(KnowledgeIngestionRunOutcome.Completed, outcome);
        AssertEx.Equal(before, await ReadAsync(databasePath, documentId), "A row this run no longer owns must not be touched.");
        await _notifier.DidNotReceive().NotifyDocumentChangedAsync(documentId, KnowledgeDocumentStatus.Pending, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_WhenTheReleaseFailsTwice_RetriesAndReleasesOnce()
    {
        var databasePath = await CreateDatabaseAsync();
        var documentId = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 1);
        await FailReleasesAsync(databasePath, times: 2);
        var clock = new RetryDelayClock();

        var run = RunAsync(databasePath, documentId, Vetoing(), clock);
        await clock.FireNextDelayAsync();
        await clock.FireNextDelayAsync();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(30));

        AssertEx.Equal(KnowledgeIngestionRunOutcome.Deferred, outcome);
        AssertEx.Equal(2, clock.DelaysArmed);
        var row = AssertEx.NotNull(await ReadAsync(databasePath, documentId));
        AssertEx.Equal(nameof(KnowledgeDocumentStatus.Pending), row.Status);
        AssertEx.Equal(1L, row.Attempts, "Two failed writes and one success must refund exactly one attempt.");
        await _notifier.Received(1).NotifyDocumentChangedAsync(documentId, KnowledgeDocumentStatus.Pending, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_WhenEveryReleaseFails_LogsAnErrorAndLeavesTheRowInProgressNotFailed()
    {
        var databasePath = await CreateDatabaseAsync();
        var documentId = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0);
        await FailReleasesAsync(databasePath, times: KnowledgeIngestionService.MaxVetoReleaseAttempts);
        var clock = new RetryDelayClock();
        var logger = new RecordingLogger<KnowledgeIngestionService>();

        var run = RunAsync(databasePath, documentId, Vetoing(), clock, logger);
        for (var delay = 1; delay < KnowledgeIngestionService.MaxVetoReleaseAttempts; delay++)
        {
            await clock.FireNextDelayAsync();
        }

        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(30));

        AssertEx.Equal(KnowledgeIngestionRunOutcome.Completed, outcome);
        var row = AssertEx.NotNull(await ReadAsync(databasePath, documentId));
        AssertEx.Equal(nameof(KnowledgeDocumentStatus.Embedding), row.Status, "Startup recovery owns a row whose release never landed.");
        AssertEx.Null(row.Reason);
        AssertEx.Equal(1, logger.Entries.Count(entry => entry.Level == LogLevel.Error));
        await _notifier.DidNotReceive().NotifyDocumentChangedAsync(documentId, KnowledgeDocumentStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ListPendingDocumentIdsAsync_SkipsWaitingRowsUntilTheWaitIsCleared()
    {
        var databasePath = await CreateDatabaseAsync();
        var fresh = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0);
        var waiting = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0, KnowledgeIngestionService.WaitingForEmbeddingModelReason);
        _ = await SeedAsync(databasePath, KnowledgeDocumentStatus.Embedding, attempts: 1);

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            var catalog = CreateCatalog(context);
            var beforeClear = await catalog.ListPendingDocumentIdsAsync(CancellationToken.None);
            AssertEx.True(beforeClear.Count == 1 && beforeClear[0] == fresh, "Only the fresh Pending row is stranded work.");
            AssertEx.True(await catalog.HasEmbeddingModelWaitAsync(CancellationToken.None), "One row waits.");

            var cleared = await catalog.ClearEmbeddingModelWaitAsync(CancellationToken.None);
            AssertEx.True(cleared.Count == 1 && cleared[0] == waiting, "The clear names exactly the row it changed, so its caller can notify it.");
            var listed = await catalog.ListPendingDocumentIdsAsync(CancellationToken.None);
            AssertEx.True(listed.Count == 2 && listed.Contains(fresh) && listed.Contains(waiting), "A cleared row is stranded work again.");
            AssertEx.False(await catalog.HasEmbeddingModelWaitAsync(CancellationToken.None), "Nothing waits after the clear.");
        }

        AssertEx.Null(AssertEx.NotNull(await ReadAsync(databasePath, waiting)).Reason);
    }

    [Test]
    public async Task RunAsync_WhenARunWaitsOnAnotherRunsVetoedEmbed_BothDeferAndNeitherFails()
    {
        var databasePath = await CreateDatabaseAsync();
        var owner = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0);
        var waiter = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0);
        var ownerEmbedding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterEmbedded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var embedder = FixedWidthEmbedder();

        // The waiter claims only its extra chunk, embeds it, then waits on the owner's claim for the shared one; the
        // owner's embed is vetoed only once the waiter is already waiting, and the waiter's own retry meets the eject too.
        embedder.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(async Task<KnowledgeEmbeddingResult> (call) =>
                {
                    var texts = call.Arg<IReadOnlyList<string>>();
                    if (texts.Any(text => text.Contains("Extra", StringComparison.Ordinal)))
                    {
                        waiterEmbedded.TrySetResult();
                        return Embedded(texts);
                    }

                    ownerEmbedding.TrySetResult();
                    await waiterEmbedded.Task;
                    throw new OperatorEjectedException("The embedding model was ejected by the operator.");
                });
        var cache = NewCache();

        var ownerRun = RunAsync(databasePath, owner, embedder, cache: cache, document: BuildSharedDocument(withExtraChunk: false));
        await ownerEmbedding.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var waiterRun = RunAsync(databasePath, waiter, embedder, cache: cache, document: BuildSharedDocument(withExtraChunk: true));

        AssertEx.Equal(KnowledgeIngestionRunOutcome.Deferred, await ownerRun.WaitAsync(TimeSpan.FromSeconds(30)));
        AssertEx.Equal(KnowledgeIngestionRunOutcome.Deferred, await waiterRun.WaitAsync(TimeSpan.FromSeconds(30)), "A run coalesced onto the vetoed embed waits too.");
        foreach (var documentId in new[]
                 {
                     owner,
                     waiter
                 })
        {
            var row = AssertEx.NotNull(await ReadAsync(databasePath, documentId));
            AssertEx.Equal(nameof(KnowledgeDocumentStatus.Pending), row.Status);
            AssertEx.Equal("embedding-model-ejected", KnowledgeFailureReasonCodes.CodeOf(row.Reason));
        }

        await _notifier.DidNotReceive().NotifyDocumentChangedAsync(Arg.Any<Guid>(), KnowledgeDocumentStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_WhenTwoRunsWaitOnAVetoedOwner_EachRoundsOwnerIsVetoedAndAllThreeDefer()
    {
        var databasePath = await CreateDatabaseAsync();
        var documents = new[]
        {
            await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0),
            await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0),
            await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0)
        };
        var cache = new EntryCountingCache(NewCache());
        var ownerEmbedding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var embeds = 0;

        // Each round's owner is vetoed only once every run still in play has joined its claim: round one has three
        // runs in the cache (A owns, B and C wait), round two the two retries (one owns, one waits), round three the last.
        int[] joinedBeforeVeto = [3, 5, 6];
        var embedder = FixedWidthEmbedder();
        embedder.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(async Task<KnowledgeEmbeddingResult> (_) =>
                {
                    var round = Interlocked.Increment(ref embeds);
                    ownerEmbedding.TrySetResult();
                    await cache.WhenAtLeast(joinedBeforeVeto[Math.Min(round, joinedBeforeVeto.Length) - 1]);
                    throw new OperatorEjectedException("The embedding model was ejected by the operator.");
                });

        var ownerRun = RunAsync(databasePath, documents[0], embedder, cache: cache, document: BuildSharedDocument(withExtraChunk: false), maxConcurrentIngestions: 3);
        await ownerEmbedding.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var waiterRuns = new[]
        {
            RunAsync(databasePath, documents[1], embedder, cache: cache, document: BuildSharedDocument(withExtraChunk: false), maxConcurrentIngestions: 3),
            RunAsync(databasePath, documents[2], embedder, cache: cache, document: BuildSharedDocument(withExtraChunk: false), maxConcurrentIngestions: 3)
        };

        var outcomes = await Task.WhenAll([ownerRun, .. waiterRuns]).WaitAsync(TimeSpan.FromSeconds(30));

        AssertEx.True(outcomes.All(outcome => outcome == KnowledgeIngestionRunOutcome.Deferred), "A waiter that joins a second vetoed owner must defer too, not fail.");
        AssertEx.Equal(3, Volatile.Read(ref embeds), "Each round's owner embeds once and meets the eject.");
        foreach (var documentId in documents)
        {
            var row = AssertEx.NotNull(await ReadAsync(databasePath, documentId));
            AssertEx.Equal(nameof(KnowledgeDocumentStatus.Pending), row.Status);
            AssertEx.Equal("embedding-model-ejected", KnowledgeFailureReasonCodes.CodeOf(row.Reason));
        }

        await _notifier.DidNotReceive().NotifyDocumentChangedAsync(Arg.Any<Guid>(), KnowledgeDocumentStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_WhenAReloadClearedTheMarkBeforeTheWaiterResumes_TheWaiterEmbedsItselfAndIndexes()
    {
        var databasePath = await CreateDatabaseAsync();
        var owner = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0);
        var waiter = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0);
        var ownerEmbedding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterEmbedded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedCalls = 0;
        var embedder = FixedWidthEmbedder();

        // The owner's veto lands, then a user reload clears the mark, so the waiter's own retry of the shared chunk embeds.
        embedder.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(async Task<KnowledgeEmbeddingResult> (call) =>
                {
                    var texts = call.Arg<IReadOnlyList<string>>();
                    if (texts.Any(text => text.Contains("Extra", StringComparison.Ordinal)))
                    {
                        waiterEmbedded.TrySetResult();
                        return Embedded(texts);
                    }

                    if (Interlocked.Increment(ref sharedCalls) > 1)
                    {
                        return Embedded(texts);
                    }

                    ownerEmbedding.TrySetResult();
                    await waiterEmbedded.Task;
                    throw new OperatorEjectedException("The embedding model was ejected by the operator.");
                });
        var cache = NewCache();
        var indexWriter = Substitute.For<IKnowledgeIndexWriter>();
        indexWriter.WriteAsync(Arg.Any<KnowledgeIndexInput>(), Arg.Any<CancellationToken>()).Returns(true);

        var ownerRun = RunAsync(databasePath, owner, embedder, cache: cache, document: BuildSharedDocument(withExtraChunk: false), indexWriter: indexWriter);
        await ownerEmbedding.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var waiterRun = RunAsync(databasePath, waiter, embedder, cache: cache, document: BuildSharedDocument(withExtraChunk: true), indexWriter: indexWriter);

        AssertEx.Equal(KnowledgeIngestionRunOutcome.Deferred, await ownerRun.WaitAsync(TimeSpan.FromSeconds(30)));
        AssertEx.Equal(KnowledgeIngestionRunOutcome.Completed, await waiterRun.WaitAsync(TimeSpan.FromSeconds(30)));
        AssertEx.Equal(2, Volatile.Read(ref sharedCalls), "The waiter embeds the shared chunk itself, once.");
        _ = await indexWriter.Received(1).WriteAsync(Arg.Is<KnowledgeIndexInput>(input => input.DocumentId == waiter), Arg.Any<CancellationToken>());
        await _notifier.Received(1).NotifyDocumentChangedAsync(waiter, KnowledgeDocumentStatus.Indexed, Arg.Any<CancellationToken>());
        await _notifier.DidNotReceive().NotifyDocumentChangedAsync(Arg.Any<Guid>(), KnowledgeDocumentStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_WhenTheOwnerHitAnOutageWhileTheInventoryIsUnreadable_TheWaiterFailsWithItsOwnCauseNotTheEjectHint()
    {
        var databasePath = await CreateDatabaseAsync();
        var owner = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0);
        var waiter = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0);
        var ownerEmbedding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterEmbedded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedCalls = 0;
        var embedder = FixedWidthEmbedder();

        // An unreadable inventory reads as blocked, yet nobody ejected anything: the shared chunk's process is simply down.
        embedder.IsEmbeddingModelBlockedAsync(Arg.Any<CancellationToken>()).Returns(true);
        embedder.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(async Task<KnowledgeEmbeddingResult> (call) =>
                {
                    var texts = call.Arg<IReadOnlyList<string>>();
                    if (texts.Any(text => text.Contains("Extra", StringComparison.Ordinal)))
                    {
                        waiterEmbedded.TrySetResult();
                        return Embedded(texts);
                    }

                    _ = Interlocked.Increment(ref sharedCalls);
                    ownerEmbedding.TrySetResult();
                    await waiterEmbedded.Task;
                    throw new KnowledgeIngestionException(KnowledgeChunkEmbedder.EmbeddingUnreachableReason, new IOException("The embedding process went away."));
                });
        var cache = NewCache();

        var ownerRun = RunAsync(databasePath, owner, embedder, cache: cache, document: BuildSharedDocument(withExtraChunk: false));
        await ownerEmbedding.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var waiterRun = RunAsync(databasePath, waiter, embedder, cache: cache, document: BuildSharedDocument(withExtraChunk: true));

        AssertEx.Equal(KnowledgeIngestionRunOutcome.Completed, await ownerRun.WaitAsync(TimeSpan.FromSeconds(30)));
        AssertEx.Equal(KnowledgeIngestionRunOutcome.Completed, await waiterRun.WaitAsync(TimeSpan.FromSeconds(30)), "An outage is not an eject: the waiter must not park.");
        AssertEx.Equal(2, Volatile.Read(ref sharedCalls), "The waiter retries the shared chunk itself, once.");
        foreach (var documentId in new[]
                 {
                     owner,
                     waiter
                 })
        {
            var row = AssertEx.NotNull(await ReadAsync(databasePath, documentId));
            AssertEx.Equal(nameof(KnowledgeDocumentStatus.Failed), row.Status);
            AssertEx.Equal(KnowledgeChunkEmbedder.EmbeddingUnreachableReason, row.Reason);
        }
    }

    [Test]
    public async Task RunAsync_AfterAVetoedRun_ALaterRunOnTheSameChunksEmbedsAndIndexes()
    {
        var databasePath = await CreateDatabaseAsync();
        var vetoed = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0);
        var later = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0);
        var calls = 0;
        var embedder = FixedWidthEmbedder();

        // The first embed is the eject's veto; a user reload follows, and every later embed succeeds.
        embedder.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(call => Interlocked.Increment(ref calls) == 1
                    ? Task.FromException<KnowledgeEmbeddingResult>(new OperatorEjectedException("The embedding model was ejected by the operator."))
                    : Task.FromResult(Embedded(call.Arg<IReadOnlyList<string>>())));
        var cache = NewCache();
        var indexWriter = Substitute.For<IKnowledgeIndexWriter>();
        indexWriter.WriteAsync(Arg.Any<KnowledgeIndexInput>(), Arg.Any<CancellationToken>()).Returns(true);

        AssertEx.Equal(KnowledgeIngestionRunOutcome.Deferred,
            await RunAsync(databasePath, vetoed, embedder, cache: cache, document: BuildSharedDocument(withExtraChunk: false), indexWriter: indexWriter));
        AssertEx.Equal(KnowledgeIngestionRunOutcome.Completed,
            await RunAsync(databasePath, later, embedder, cache: cache, document: BuildSharedDocument(withExtraChunk: false), indexWriter: indexWriter));

        _ = await indexWriter.Received(1).WriteAsync(Arg.Is<KnowledgeIndexInput>(input => input.DocumentId == later), Arg.Any<CancellationToken>());
        AssertEx.Equal(nameof(KnowledgeDocumentStatus.Pending), AssertEx.NotNull(await ReadAsync(databasePath, vetoed)).Status);
    }

    [Test]
    public async Task RunAsync_WhenTheCacheFailureRepeatsWhileNothingIsBlocked_RetriesOnceAndFails()
    {
        var databasePath = await CreateDatabaseAsync();
        var documentId = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0);
        var embedder = FixedWidthEmbedder();

        // No vectors for a non-empty batch: the cache rejects it as incomplete, the same way on every attempt.
        embedder.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Embedded([])));

        var outcome = await RunAsync(databasePath, documentId, embedder, cache: NewCache());

        AssertEx.Equal(KnowledgeIngestionRunOutcome.Completed, outcome);
        _ = await embedder.Received(2).EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        var row = AssertEx.NotNull(await ReadAsync(databasePath, documentId));
        AssertEx.Equal(nameof(KnowledgeDocumentStatus.Failed), row.Status);
        AssertEx.Equal(KnowledgeIngestionService.UnexpectedReason, row.Reason);
    }

    [Test]
    public async Task RunAsync_WhenAWaitingRowsPinnedModelWasRemoved_TheWakeReAdmitsItAndItFailsWithThePinnedModelMissingReason()
    {
        var databasePath = await CreateDatabaseAsync();
        var documentId = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0, KnowledgeIngestionService.WaitingForEmbeddingModelReason);

        // The operator ejected the pinned embedder, then deleted it: the inventory no longer lists it, but the supervisor's
        // eject mark for it stays, so any background load of it is still vetoed.
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
                 .Returns<Task<GeneratedEmbeddings<Embedding<float>>>>(_ => throw new OperatorEjectedException("The embedding model was ejected by the operator."));
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ListModelsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<LocalModelDescriptor>>([
            new LocalModelDescriptor
            {
                ModelName = "other-embed.gguf",
                ProviderName = LlamaServerProviderConstants.ProviderName,
                IsAvailable = true,
                SizeBytes = 1024,
                ModifiedAt = DateTimeOffset.UnixEpoch,
                MaxContextTokens = null,
                Capabilities = []
            }
        ]));
        provider.CreateEmbeddingGenerator(Arg.Any<LocalModelSelection>()).Returns(generator);
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProvider(Arg.Any<string>()).Returns(provider);
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.IsBackgroundLaunchBlocked(Arg.Any<string>(), Arg.Any<ModelRole>()).Returns(true);
        var options = Options.Create(new KnowledgeBaseOptions
        {
            EmbeddingProviderName = LlamaServerProviderConstants.ProviderName
        });
        var embedder = new KnowledgeChunkEmbedder(providerResolver,
            new EmbeddingModelResolver(options, new FakeNodeSettingsStore(new StoredNodeSettings
            {
                KnowledgeEmbeddingModelName = "removed-embed.gguf"
            })),
            new KnowledgeEmbeddingPrefixer(),
            options,
            supervisor);

        // The wake: the removed pin reads as not blocked, so it clears the wait and the sweep runs the row again.
        AssertEx.False(await embedder.IsEmbeddingModelBlockedAsync(CancellationToken.None));
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            _ = await CreateCatalog(context).ClearEmbeddingModelWaitAsync(CancellationToken.None);
        }

        var outcome = await RunAsync(databasePath, documentId, embedder);

        AssertEx.Equal(KnowledgeIngestionRunOutcome.Completed, outcome, "The run must not park again behind a mark that can never clear.");
        var row = AssertEx.NotNull(await ReadAsync(databasePath, documentId));
        AssertEx.Equal(nameof(KnowledgeDocumentStatus.Failed), row.Status);
        AssertEx.Equal(KnowledgeChunkEmbedder.EmbeddingPinnedModelMissingReason, row.Reason);
        AssertEx.Equal("embedding-pinned-model-missing", KnowledgeFailureReasonCodes.CodeOf(row.Reason));
    }

    [Test]
    public async Task RunAsync_WhenAWaitingRowsUnpinnedEmbedderWasRemoved_TheWakeReAdmitsItAndItFailsAsUnavailable()
    {
        var databasePath = await CreateDatabaseAsync();
        var documentId = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0, KnowledgeIngestionService.WaitingForEmbeddingModelReason);

        // Nothing is pinned yet: the operator ejected the only embedder, then deleted it. The inventory answers with a chat
        // model alone, and every eject mark stays, so a background load of any name would still be vetoed.
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ListModelsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<LocalModelDescriptor>>([
            new LocalModelDescriptor
            {
                ModelName = "qwen2.5:Q4_K_M",
                ProviderName = LlamaServerProviderConstants.ProviderName,
                IsAvailable = true,
                SizeBytes = 1024,
                ModifiedAt = DateTimeOffset.UnixEpoch,
                MaxContextTokens = null,
                Capabilities = []
            }
        ]));
        provider.CreateEmbeddingGenerator(Arg.Any<LocalModelSelection>())
                .Returns(_ => throw new OperatorEjectedException("The embedding model was ejected by the operator."));
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProvider(Arg.Any<string>()).Returns(provider);
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.IsBackgroundLaunchBlocked(Arg.Any<string>(), Arg.Any<ModelRole>()).Returns(true);
        var options = Options.Create(new KnowledgeBaseOptions
        {
            EmbeddingProviderName = LlamaServerProviderConstants.ProviderName
        });
        var embedder = new KnowledgeChunkEmbedder(providerResolver,
            new EmbeddingModelResolver(options, new FakeNodeSettingsStore(new StoredNodeSettings())),
            new KnowledgeEmbeddingPrefixer(),
            options,
            supervisor);

        AssertEx.False(await embedder.IsEmbeddingModelBlockedAsync(CancellationToken.None), "A removed unpinned embedder must not strand waiting rows.");
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            _ = await CreateCatalog(context).ClearEmbeddingModelWaitAsync(CancellationToken.None);
        }

        var outcome = await RunAsync(databasePath, documentId, embedder);

        AssertEx.Equal(KnowledgeIngestionRunOutcome.Completed, outcome, "The run must not park again behind a mark that can never clear.");
        var row = AssertEx.NotNull(await ReadAsync(databasePath, documentId));
        AssertEx.Equal(nameof(KnowledgeDocumentStatus.Failed), row.Status);
        AssertEx.Equal(KnowledgeChunkEmbedder.EmbeddingUnavailableReason, row.Reason);
        _ = provider.DidNotReceiveWithAnyArgs().CreateEmbeddingGenerator(default!);
    }

    [Test]
    public async Task RunAsync_WhenAFreshUploadMeetsAnEjectedEmbedder_ParksWithoutExtracting()
    {
        var databasePath = await CreateDatabaseAsync();
        var documentId = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 1);
        var embedder = FixedWidthEmbedder();
        embedder.IsEmbeddingModelEjectedAsync(Arg.Any<CancellationToken>()).Returns(true);
        var extractor = ExtractingExtractor();

        var outcome = await RunAsync(databasePath, documentId, embedder, extractor: extractor);

        AssertEx.Equal(KnowledgeIngestionRunOutcome.Deferred, outcome);
        _ = await extractor.DidNotReceiveWithAnyArgs().ExtractStructuredAsync(default!, default!, default!, default);
        var row = AssertEx.NotNull(await ReadAsync(databasePath, documentId));
        AssertEx.Equal(nameof(KnowledgeDocumentStatus.Pending), row.Status);
        AssertEx.Equal("embedding-model-ejected", KnowledgeFailureReasonCodes.CodeOf(row.Reason));
        AssertEx.Equal(1L, row.Attempts, "The parked start hands its attempt back.");
        await _notifier.DidNotReceive().NotifyDocumentChangedAsync(documentId, KnowledgeDocumentStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_WhenAnIndexedRowIsReindexedWhileTheEmbedderIsEjected_StillExtracts()
    {
        var databasePath = await CreateDatabaseAsync();
        var documentId = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0, chunkCount: 3);
        var embedder = Vetoing();
        embedder.IsEmbeddingModelEjectedAsync(Arg.Any<CancellationToken>()).Returns(true);
        var extractor = ExtractingExtractor();

        var outcome = await RunAsync(databasePath, documentId, embedder, extractor: extractor);

        AssertEx.Equal(KnowledgeIngestionRunOutcome.Deferred, outcome, "The embed's own veto still parks it.");
        _ = await extractor.Received(1).ExtractStructuredAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _ = await embedder.Received(1).EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_WhenAReplacedRevisionsChunksWouldAllHitTheCache_StillParksBeforeExtracting()
    {
        var databasePath = await CreateDatabaseAsync();
        await using var services = BuildStoreServices(databasePath);
        var store = CreateStore(services);
        var documentId = Guid.NewGuid();
        _ = await store.AddAsync(RepositoryInput(documentId, "original source"), CancellationToken.None);
        await ExecuteAsync(databasePath, "UPDATE knowledge_documents SET status = 'Indexed', chunk_count = 3 WHERE document_id = $id;", documentId);
        AssertEx.True((await store.AddAsync(RepositoryInput(Guid.NewGuid(), "changed source"), CancellationToken.None)).WasUpdated,
            "Precondition: the changed bytes replace the indexed revision in place.");
        var embedder = FixedWidthEmbedder();
        embedder.IsEmbeddingModelEjectedAsync(Arg.Any<CancellationToken>()).Returns(true);
        var extractor = ExtractingExtractor();

        var outcome = await RunAsync(databasePath, documentId, embedder, cache: AllHitCache(), extractor: extractor);

        AssertEx.Equal(KnowledgeIngestionRunOutcome.Deferred, outcome, "A replacement keeps no chunks, so it waits even if every chunk were cached.");
        _ = await extractor.DidNotReceiveWithAnyArgs().ExtractStructuredAsync(default!, default!, default!, default);
        _ = await embedder.DidNotReceiveWithAnyArgs().EmbedAsync(default!, default);
        AssertEx.Equal("embedding-model-ejected", KnowledgeFailureReasonCodes.CodeOf(AssertEx.NotNull(await ReadAsync(databasePath, documentId)).Reason));
    }

    [Test]
    public async Task RunAsync_WhenARepairedByteIdenticalUploadMeetsAnEjectedEmbedder_ParksWithoutExtracting()
    {
        var databasePath = await CreateDatabaseAsync();
        await using var services = BuildStoreServices(databasePath);
        var store = CreateStore(services);
        var documentId = Guid.NewGuid();
        var content = Encoding.UTF8.GetBytes("bytes whose blob a crash lost");
        _ = await store.AddAsync(UploadInput(documentId, content), CancellationToken.None);
        await ExecuteAsync(databasePath, "UPDATE knowledge_documents SET status = 'Indexed', chunk_count = 3 WHERE document_id = $id;", documentId);
        File.Delete(Path.Combine(_rootPath, "data", "knowledge-base", "documents", documentId.ToString("D") + ".txt"));
        AssertEx.Equal(documentId, (await store.AddAsync(UploadInput(Guid.NewGuid(), content), CancellationToken.None)).DocumentId,
            "Precondition: the re-upload repairs the existing row.");
        var embedder = FixedWidthEmbedder();
        embedder.IsEmbeddingModelEjectedAsync(Arg.Any<CancellationToken>()).Returns(true);
        var extractor = ExtractingExtractor();

        var outcome = await RunAsync(databasePath, documentId, embedder, extractor: extractor);

        AssertEx.Equal(KnowledgeIngestionRunOutcome.Deferred, outcome, "The repair zeroed the chunk count, so the row waits like a fresh upload.");
        _ = await extractor.DidNotReceiveWithAnyArgs().ExtractStructuredAsync(default!, default!, default!, default);
        AssertEx.Equal(nameof(KnowledgeDocumentStatus.Pending), AssertEx.NotNull(await ReadAsync(databasePath, documentId)).Status);
    }

    [Test]
    public async Task RunAsync_WhenTheIndexCommits_LogsTheIndexedDocument()
    {
        var databasePath = await CreateDatabaseAsync();
        var documentId = await SeedAsync(databasePath, KnowledgeDocumentStatus.Pending, attempts: 0);
        var embedder = FixedWidthEmbedder();
        embedder.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(Embedded(call.Arg<IReadOnlyList<string>>())));
        var indexWriter = Substitute.For<IKnowledgeIndexWriter>();
        indexWriter.WriteAsync(Arg.Any<KnowledgeIndexInput>(), Arg.Any<CancellationToken>()).Returns(true);
        var logger = new RecordingLogger<KnowledgeIngestionService>();

        AssertEx.Equal(KnowledgeIngestionRunOutcome.Completed, await RunAsync(databasePath, documentId, embedder, logger: logger, indexWriter: indexWriter));

        AssertEx.Equal(1, logger.CountContaining($"Knowledge document {documentId} indexed", LogLevel.Information), logger.AllText);
    }

    // An embedder whose fixed-width descriptor routes the run through the chunk embedding cache.
    private static IKnowledgeChunkEmbedder FixedWidthEmbedder()
    {
        var embedder = Substitute.For<IKnowledgeChunkEmbedder>();
        embedder.ResolveExpectedVectorAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<KnowledgeEmbeddingDescriptor?>(new KnowledgeEmbeddingDescriptor
        {
            ResolvedModel = "nomic-embed-text",
            VectorIdentity = "test-identity",
            Dimension = 4
        }));
        return embedder;
    }

    private static KnowledgeEmbeddingResult Embedded(IReadOnlyList<string> texts)
    {
        return new KnowledgeEmbeddingResult
        {
            Vectors = [.. texts.Select(static _ => new byte[4 * sizeof(float)])],
            ResolvedModel = "nomic-embed-text",
            VectorIdentity = "test-identity",
            Dimension = 4
        };
    }

    // The real cache over an empty committed-vector store, so every chunk misses durably.
    private static KnowledgeChunkEmbeddingCache NewCache()
    {
        var reuseStore = Substitute.For<IKnowledgeChunkEmbeddingReuseStore>();
        reuseStore.FindManyAsync(Arg.Any<IReadOnlyList<KnowledgeChunkEmbeddingCacheKey>>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<IReadOnlyDictionary<KnowledgeChunkEmbeddingCacheKey, byte[]>>(new Dictionary<KnowledgeChunkEmbeddingCacheKey, byte[]>()));
        return new KnowledgeChunkEmbeddingCache(reuseStore, Options.Create(new KnowledgeBaseOptions()), TimeProvider.System);
    }

    private async Task<KnowledgeIngestionRunOutcome> RunAsync(string databasePath,
        Guid documentId,
        IKnowledgeChunkEmbedder embedder,
        TimeProvider? timeProvider = null,
        ILogger<KnowledgeIngestionService>? logger = null,
        IKnowledgeChunkEmbeddingCache? cache = null,
        IngestionDocument? document = null,
        IKnowledgeIndexWriter? indexWriter = null,
        int maxConcurrentIngestions = 1,
        IDocumentTextExtractor? extractor = null)
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var options = Options.Create(new KnowledgeBaseOptions
        {
            MaxConcurrentIngestions = maxConcurrentIngestions
        });
        var blobStore = Substitute.For<IKnowledgeDocumentBlobStore>();
        blobStore.ReadBytesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<byte[]?>([1, 2, 3]));

        var service = new KnowledgeIngestionService(context,
            blobStore,
            extractor ?? ExtractingExtractor(document),
            new HeaderBoundaryChunkingService(options),
            embedder,
            indexWriter ?? Substitute.For<IKnowledgeIndexWriter>(),
            _notifier,
            timeProvider ?? TimeProvider.System,
            logger ?? new RecordingLogger<KnowledgeIngestionService>(),
            cache,
            options);
        return await service.RunAsync(documentId, CancellationToken.None);
    }

    private static IDocumentTextExtractor ExtractingExtractor(IngestionDocument? document = null)
    {
        var extractor = Substitute.For<IDocumentTextExtractor>();
        extractor.ExtractStructuredAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new DocumentStructuredExtractionResult
                 {
                     Status = DocumentExtractionStatus.Extracted,
                     Document = document ?? BuildDocument(),
                     Error = null
                 }));
        return extractor;
    }

    // The real cache over a committed-vector store that already holds every key, so no chunk would need the embedder.
    private static KnowledgeChunkEmbeddingCache AllHitCache()
    {
        var reuseStore = Substitute.For<IKnowledgeChunkEmbeddingReuseStore>();
        reuseStore.FindManyAsync(Arg.Any<IReadOnlyList<KnowledgeChunkEmbeddingCacheKey>>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                  .Returns(call => Task.FromResult<IReadOnlyDictionary<KnowledgeChunkEmbeddingCacheKey, byte[]>>(call.Arg<IReadOnlyList<KnowledgeChunkEmbeddingCacheKey>>().Distinct()
                                                                                                                     .ToDictionary(static key => key, static _ => new byte[4 * sizeof(float)])));
        return new KnowledgeChunkEmbeddingCache(reuseStore, Options.Create(new KnowledgeBaseOptions()), TimeProvider.System);
    }

    // The real blob store, so a replacement or a repair leaves the row exactly as an upload would.
    private ServiceProvider BuildStoreServices(string databasePath)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder));
        return services.BuildServiceProvider();
    }

    private KnowledgeDocumentBlobStore CreateStore(ServiceProvider services)
    {
        return new KnowledgeDocumentBlobStore(services.GetRequiredService<IServiceScopeFactory>(),
            new FakeNodeDataDirectory(Path.Combine(_rootPath, "data")),
            _keyHolder,
            TimeProvider.System);
    }

    private static KnowledgeDocumentInput UploadInput(Guid documentId, byte[] content)
    {
        return new KnowledgeDocumentInput
        {
            DocumentId = documentId,
            OriginalFileName = "notes.txt",
            MimeType = "text/plain",
            Extension = ".txt",
            SizeBytes = content.Length,
            ContentHash = Convert.ToHexString(SHA256.HashData(content)),
            Content = content,
            EmbeddingModel = "nomic-embed-text"
        };
    }

    private static KnowledgeDocumentInput RepositoryInput(Guid documentId, string text)
    {
        var content = Encoding.UTF8.GetBytes(text);
        return new KnowledgeDocumentInput
        {
            DocumentId = documentId,
            OriginalFileName = "src/Widget.cs",
            MimeType = "text/plain",
            Extension = ".cs",
            SizeBytes = content.Length,
            ContentHash = Convert.ToHexString(SHA256.HashData(content)),
            Content = content,
            EmbeddingModel = "nomic-embed-text",
            CollectionId = "REPOSITORY",
            SourcePath = "src/Widget.cs",
            SourceKind = "repository",
            SourceId = "repository-a"
        };
    }

    // An embedder whose model the operator ejected: every embed is the typed veto, after an optional concurrent change.
    private static IKnowledgeChunkEmbedder Vetoing(Func<Task>? meanwhile = null)
    {
        var embedder = Substitute.For<IKnowledgeChunkEmbedder>();
        embedder.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(async Task<KnowledgeEmbeddingResult> (_) =>
                {
                    if (meanwhile is not null)
                    {
                        await meanwhile();
                    }

                    throw new OperatorEjectedException("The embedding model was ejected by the operator.");
                });
        return embedder;
    }

    private static KnowledgeDocumentCatalogService CreateCatalog(NodeChatDbContext context)
    {
        var options = Options.Create(new KnowledgeBaseOptions());
        return new KnowledgeDocumentCatalogService(context,
            Substitute.For<ILocalModelProviderResolver>(),
            new EmbeddingModelResolver(options, new FakeNodeSettingsStore(new StoredNodeSettings())),
            options,
            TimeProvider.System);
    }

    private static IngestionDocument BuildDocument()
    {
        var document = new IngestionDocument("test-document");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph("Some text to embed.")
        {
            Text = "Some text to embed."
        });
        document.Sections.Add(section);
        return document;
    }

    // "Alpha" is the chunk both documents share; "Beta" is the waiting run's own extra chunk.
    private static IngestionDocument BuildSharedDocument(bool withExtraChunk)
    {
        var document = new IngestionDocument("test-document");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentHeader("Alpha")
        {
            Text = "Alpha",
            Level = 1
        });
        section.Elements.Add(new IngestionDocumentParagraph("Shared body text.")
        {
            Text = "Shared body text."
        });
        if (withExtraChunk)
        {
            section.Elements.Add(new IngestionDocumentHeader("Beta")
            {
                Text = "Beta",
                Level = 1
            });
            section.Elements.Add(new IngestionDocumentParagraph("Extra body text.")
            {
                Text = "Extra body text."
            });
        }

        document.Sections.Add(section);
        return document;
    }

    // The release UPDATE alone fails `times` times. RAISE(FAIL) keeps the trigger's own counter decrement.
    private static async Task FailReleasesAsync(string databasePath, int times)
    {
        var waiting = KnowledgeIngestionService.WaitingForEmbeddingModelReason;
        await ExecuteAsync(databasePath,
            $"""
             CREATE TABLE release_failures (remaining INTEGER NOT NULL);
             INSERT INTO release_failures VALUES ({times.ToString(CultureInfo.InvariantCulture)});
             CREATE TRIGGER fail_release BEFORE UPDATE ON knowledge_documents
             WHEN NEW.failure_reason = '{waiting}' AND (SELECT remaining FROM release_failures) > 0
             BEGIN
                 UPDATE release_failures SET remaining = remaining - 1;
                 SELECT RAISE(FAIL, 'injected release failure');
             END;
             """,
            documentId: null);
    }

    private async Task<string> CreateDatabaseAsync()
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, Guid.NewGuid().ToString("N") + ".sqlite");
        await MigratedDatabaseTemplate.CopyChatHeadAsync(databasePath);
        return databasePath;
    }

    private static async Task<Guid> SeedAsync(string databasePath, KnowledgeDocumentStatus status, int attempts, string? reason = null, int chunkCount = 0)
    {
        var documentId = Guid.NewGuid();
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO knowledge_documents (document_id, original_file_name, mime_type, extension, size_bytes, content_hash, storage_path, status, failure_reason, chunk_count, embedding_model, ingestion_attempts, created_at_utc, updated_at_utc)
            VALUES ($id, $name, 'text/plain', '.txt', 10, $hash, $path, $status, $reason, $chunk_count, 'nomic-embed-text', $attempts, 1, 1);
            """;
        command.Parameters.AddWithValue("$chunk_count", chunkCount);
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$name", new byte[]
        {
            1,
            2,
            3
        });
        command.Parameters.AddWithValue("$hash", "hash-" + documentId.ToString("N"));
        command.Parameters.AddWithValue("$path", documentId.ToString("D") + ".txt");
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$attempts", attempts);
        _ = await command.ExecuteNonQueryAsync();
        return documentId;
    }

    private static async Task ExecuteAsync(string databasePath, string sql, Guid? documentId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Fixed literals from this suite; the document id is a bound parameter.
        command.CommandText = sql;
#pragma warning restore CA2100
        if (documentId is { } id)
        {
            command.Parameters.AddWithValue("$id", id);
        }

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<DocumentRow?> ReadAsync(string databasePath, Guid documentId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, failure_reason, ingestion_attempts, content_hash FROM knowledge_documents WHERE document_id = $id;";
        command.Parameters.AddWithValue("$id", documentId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new DocumentRow
        {
            Status = reader.GetString(0),
            Reason = await reader.IsDBNullAsync(1) ? null : reader.GetString(1),
            Attempts = reader.GetInt64(2),
            ContentHash = reader.GetString(3)
        };
    }

    private sealed record DocumentRow
    {
        public required string Status { get; init; }

        public string? Reason { get; init; }

        public required long Attempts { get; init; }

        public required string ContentHash { get; init; }
    }

    /// <summary>
    ///     The real cache, counting each call once it has claimed or joined its keys: the inner call runs synchronously up
    ///     to its first wait, so it returns only after its claims or waits are registered.
    /// </summary>
    private sealed class EntryCountingCache : IKnowledgeChunkEmbeddingCache
    {
        private readonly IKnowledgeChunkEmbeddingCache _inner;
        private readonly Lock _gate = new();
        private readonly Dictionary<int, TaskCompletionSource> _waiters = [];
        private int _entries;

        public EntryCountingCache(IKnowledgeChunkEmbeddingCache inner)
        {
            _inner = inner;
        }

        public Task WhenAtLeast(int entries)
        {
            lock (_gate)
            {
                if (_entries >= entries)
                {
                    return Task.CompletedTask;
                }

                if (!_waiters.TryGetValue(entries, out var waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters[entries] = waiter;
                }

                return waiter.Task;
            }
        }

        public async Task<IReadOnlyList<byte[]>> GetOrCreateManyAsync(IReadOnlyList<KnowledgeChunkEmbeddingCacheKey> keys,
            Func<IReadOnlyList<KnowledgeChunkEmbeddingCacheKey>, CancellationToken, Task<IReadOnlyList<byte[]>>> factory,
            CancellationToken cancellationToken)
        {
            var resolving = _inner.GetOrCreateManyAsync(keys, factory, cancellationToken);
            lock (_gate)
            {
                _entries++;
                foreach (var reached in _waiters.Keys.Where(entries => entries <= _entries).ToList())
                {
                    _waiters[reached].TrySetResult();
                    _ = _waiters.Remove(reached);
                }
            }

            return await resolving;
        }
    }

    /// <summary>Hands out the release's retry delays one at a time, as the test fires them; every other timer never fires.</summary>
    private sealed class RetryDelayClock : TimeProvider
    {
        private readonly Channel<Action> _armed = Channel.CreateUnbounded<Action>();
        private int _delaysArmed;

        public int DelaysArmed => Volatile.Read(ref _delaysArmed);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == KnowledgeIngestionService.VetoReleaseRetryDelay)
            {
                _ = Interlocked.Increment(ref _delaysArmed);
                _ = _armed.Writer.TryWrite(() => callback(state));
            }

            return new InertTimer();
        }

        public async Task FireNextDelayAsync()
        {
            var fire = await _armed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            fire();
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
}
