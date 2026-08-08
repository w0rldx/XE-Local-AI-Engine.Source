namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     The RESOLVED embedding model name (from <see cref="IEmbeddingModelResolver" />) — not the configured name — is the
///     single identity used as the stamped <c>embedding_model</c>, the chunk-vector scope key, the search vector filter,
///     and the staleness comparison. These tests drive the real ingestion writer, catalog service, and search service
///     against a provider whose installed set makes the resolver pick a GGUF name that differs from the configured name,
///     so a same-dimension model swap is detectable (the reviewer's latent silent-corruption gap).
/// </summary>
public sealed class KnowledgeEmbeddingModelIdentityTests : IDisposable
{
    private const string ConfiguredName = "nomic-embed-text";
    private const string ResolvedGgufName = "nomic-ai/nomic-embed-text-v1.5-GGUF:Q4_K_M";

    private const string ResolvedVectorIdentity =
        "nomic-ai/nomic-embed-text-v1.5-GGUF:Q4_K_M::layernorm-population-eps1e-5-truncate-l2:v1:512";

    private const int Dimensions = 768;

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
    public async Task RunAsync_StampsResolvedModelOnDocumentRowAndEveryChunkVector()
    {
        var databasePath = GetDatabasePath("ingestion-stamp.sqlite");
        var documentId = Guid.NewGuid();

        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentId, ConfiguredName, KnowledgeDocumentStatus.Pending).ConfigureAwait(false);

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await EnsureForeignKeysOffAsync(context.Database.GetDbConnection()).ConfigureAwait(false);
            var service = CreateIngestionService(context);
            await service.RunAsync(documentId, CancellationToken.None).ConfigureAwait(false);
        }

        var stampedDocumentModel = await ReadDocumentModelAsync(databasePath, documentId).ConfigureAwait(false);
        var vectorModels = await ReadVectorModelsAsync(databasePath, documentId).ConfigureAwait(false);
        var documentIdentity = await ReadDocumentVectorIdentityAsync(databasePath, documentId).ConfigureAwait(false);
        var vectorIdentities = await ReadVectorIdentitiesAsync(databasePath, documentId).ConfigureAwait(false);

        AssertEx.Equal(ResolvedGgufName, stampedDocumentModel);
        AssertEx.True(vectorModels.Count > 0, "Ingestion should have written at least one chunk vector.");
        AssertEx.True(vectorModels.All(model => string.Equals(model, ResolvedGgufName, StringComparison.Ordinal)),
            "Every chunk vector must be keyed by the resolved model name, not the configured name.");
        AssertEx.Equal((ResolvedVectorIdentity, 512), documentIdentity);
        AssertEx.True(vectorIdentities.All(identity => identity == (ResolvedVectorIdentity, 512)),
            "Every vector row must carry the exact canonical transform identity and defensive width.");
    }

    [Test]
    public async Task ListAsync_FlagsStaleOnlyWhenStoredModelDiffersFromResolved()
    {
        var databasePath = GetDatabasePath("catalog-stale.sqlite");
        var freshId = Guid.NewGuid();
        var staleId = Guid.NewGuid();
        var pendingId = Guid.NewGuid();

        await MigrateAsync(databasePath).ConfigureAwait(false);
        // freshId was embedded by the model the resolver picks now (resolved GGUF) → not stale.
        // staleId still carries the old configured name → stale under the new resolved identity.
        // pendingId is not yet indexed and holds only the upload placeholder (configured name) → never stale.
        await SeedDocumentAsync(databasePath, freshId, ResolvedGgufName, KnowledgeDocumentStatus.Indexed).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, staleId, ConfiguredName, KnowledgeDocumentStatus.Indexed).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, pendingId, ConfiguredName, KnowledgeDocumentStatus.Pending).ConfigureAwait(false);

        IReadOnlyList<KnowledgeDocumentSummary> documents;
        await using (var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder))
        {
            await EnsureForeignKeysOffAsync(context.Database.GetDbConnection()).ConfigureAwait(false);
            documents = await CreateCatalogService(context).ListAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var fresh = documents.Single(document => document.DocumentId == freshId);
        var stale = documents.Single(document => document.DocumentId == staleId);
        var pending = documents.Single(document => document.DocumentId == pendingId);
        AssertEx.False(fresh.StaleModel, "A document embedded by the current resolved model must not be flagged stale.");
        AssertEx.True(stale.StaleModel, "A document embedded by a different model than the current resolved one must be flagged stale.");
        AssertEx.False(pending.StaleModel, "A not-yet-indexed document holding only the upload placeholder must never be flagged stale.");
    }

    [Test]
    public async Task ResetStaleDocumentsToPendingAsync_SelectsOnlyDocumentsWhoseStoredModelDiffersFromResolved()
    {
        var databasePath = GetDatabasePath("catalog-reset.sqlite");
        var freshId = Guid.NewGuid();
        var staleId = Guid.NewGuid();
        var pendingId = Guid.NewGuid();

        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, freshId, ResolvedGgufName, KnowledgeDocumentStatus.Indexed).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, staleId, ConfiguredName, KnowledgeDocumentStatus.Indexed).ConfigureAwait(false);
        // Not-yet-indexed doc carrying the upload placeholder — must NOT be reset even though its stored name differs.
        await SeedDocumentAsync(databasePath, pendingId, ConfiguredName, KnowledgeDocumentStatus.Pending).ConfigureAwait(false);

        IReadOnlyList<Guid> reset;
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await EnsureForeignKeysOffAsync(context.Database.GetDbConnection()).ConfigureAwait(false);
            reset = await CreateCatalogService(context).ResetStaleDocumentsToPendingAsync(CancellationToken.None).ConfigureAwait(false);
        }

        AssertEx.Equal(1, reset.Count);
        AssertEx.Equal(staleId, reset[0]);
        AssertEx.Equal(KnowledgeDocumentStatus.Pending.ToString(), await ReadStatusAsync(databasePath, staleId).ConfigureAwait(false));
        AssertEx.Equal(KnowledgeDocumentStatus.Indexed.ToString(), await ReadStatusAsync(databasePath, freshId).ConfigureAwait(false));
    }

    [Test]
    public async Task ListAsync_WhenPolicySwitchesToNative_FlagsTheMatryoshkaIndexStale()
    {
        var databasePath = GetDatabasePath("catalog-policy-rollback.sqlite");
        var documentId = Guid.NewGuid();
        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentId, ResolvedGgufName, KnowledgeDocumentStatus.Indexed).ConfigureAwait(false);

        IReadOnlyList<KnowledgeDocumentSummary> documents;
        await using (var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder))
        {
            await EnsureForeignKeysOffAsync(context.Database.GetDbConnection()).ConfigureAwait(false);
            documents = await CreateCatalogService(context, KnowledgeEmbeddingVectorMode.Native)
                              .ListAsync(CancellationToken.None)
                              .ConfigureAwait(false);
        }

        AssertEx.True(documents.Single().StaleModel,
            "Switching to native mode must make a 512-wide Matryoshka identity stale until a full reindex completes.");
    }

    [Test]
    public async Task SearchAsync_FiltersVectorArmByResolvedModelName()
    {
        var databasePath = GetDatabasePath("search-filter.sqlite");

        var vectorSearch = Substitute.For<IVectorSearch>();
        string? capturedModel = null;
        string? capturedIdentity = null;
        var capturedDimension = 0;
        vectorSearch.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Do<string>(model => capturedModel = model),
                        Arg.Do<string>(identity => capturedIdentity = identity),
                        Arg.Do<int>(dimension => capturedDimension = dimension),
                        Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<VectorSearchHit>>([]));

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateSearchService(context, vectorSearch);

        _ = await service.SearchAsync(new KnowledgeSearchRequest("a query", Limit: 5), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(ResolvedGgufName, capturedModel);
        AssertEx.Equal(ResolvedVectorIdentity, capturedIdentity);
        AssertEx.Equal(512, capturedDimension);
    }

    [Test]
    public async Task SearchAsync_WhenIndexedDocumentUsesLegacyVectorIdentity_DisclosesLastKnownGood()
    {
        var databasePath = GetDatabasePath("search-legacy-identity-disclosure.sqlite");
        var documentId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();

        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentId, ConfiguredName, KnowledgeDocumentStatus.Indexed).ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentId, chunkId, "legacy lexical content").ConfigureAwait(false);

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateSearchService(context,
            EmptyVectorSearch(),
            ftsHits:
            [
                new FtsSearchHit(chunkId, documentId, Bm25Score: -1.0)
            ]);

        var result = await service.SearchAsync(new KnowledgeSearchRequest("legacy lexical content", Limit: 5), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(1, result.Results.Count);
        AssertEx.Equal(KnowledgeDocumentStatus.Indexed, result.Results[0].DocumentStatus);
        AssertEx.True(result.Results[0].ServingLastKnownGood,
            "An Indexed document whose vectors do not match the current embedding identity must disclose lexical results as last-known-good until reindex.");
    }

    [Test]
    public async Task SearchAsync_WhenEmbeddingProviderIsUnavailable_DoesNotMislabelCurrentIndexedVectorsAsStale()
    {
        var databasePath = GetDatabasePath("search-current-identity-provider-outage.sqlite");
        var documentId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();

        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentId, ResolvedGgufName, KnowledgeDocumentStatus.Indexed).ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentId, chunkId, "current lexical content").ConfigureAwait(false);

        var unavailableProviderResolver = Substitute.For<ILocalModelProviderResolver>();
        unavailableProviderResolver.ResolveProvider(Arg.Any<string>())
                                   .Returns(_ => throw new InvalidOperationException("provider unavailable"));

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateSearchService(context,
            EmptyVectorSearch(),
            providerResolver: unavailableProviderResolver,
            ftsHits:
            [
                new FtsSearchHit(chunkId, documentId, Bm25Score: -1.0)
            ]);

        var result = await service.SearchAsync(new KnowledgeSearchRequest("current lexical content", Limit: 5),
            CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(1, result.Results.Count);
        AssertEx.Equal(KnowledgeDocumentStatus.Indexed, result.Results[0].DocumentStatus);
        AssertEx.False(result.Results[0].ServingLastKnownGood,
            "lexical fallback cannot infer vector staleness when no current query-vector identity was resolved");
    }

    [Test]
    public async Task SearchAsync_NativeNomicRepeatedQuery_UsesCachedCanonicalIdentityWithoutRegeneration()
    {
        var databasePath = GetDatabasePath("search-native-cache.sqlite");
        var options = Options.Create(new KnowledgeBaseOptions
        {
            EmbeddingVectorMode = KnowledgeEmbeddingVectorMode.Native
        });
        var provider = new FixedEmbeddingProvider(Descriptor(ResolvedGgufName));
        var vectorSearch = EmptyVectorSearch();
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateSearchService(context,
            vectorSearch,
            CreateProviderResolver(provider),
            new KnowledgeQueryEmbeddingCache(options),
            options);

        await service.SearchAsync(new KnowledgeSearchRequest("repeat native query", Limit: 5), CancellationToken.None).ConfigureAwait(false);
        await service.SearchAsync(new KnowledgeSearchRequest("repeat native query", Limit: 5), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(1, provider.GenerateCallCount);
        await vectorSearch.Received(2)
                          .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(),
                              ResolvedGgufName,
                              $"{ResolvedGgufName}::native:v1:{Dimensions}",
                              Dimensions,
                              Arg.Any<int>(),
                              Arg.Any<Guid?>(),
                              Arg.Any<CancellationToken>())
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task SearchAsync_NonNomicRepeatedQuery_UsesCachedCanonicalIdentityWithoutRegeneration()
    {
        const string nonNomicModel = "bge-m3";
        var databasePath = GetDatabasePath("search-non-nomic-cache.sqlite");
        var options = Options.Create(new KnowledgeBaseOptions
        {
            EmbeddingModelName = nonNomicModel
        });
        var provider = new FixedEmbeddingProvider(Descriptor(nonNomicModel));
        var vectorSearch = EmptyVectorSearch();
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateSearchService(context,
            vectorSearch,
            CreateProviderResolver(provider),
            new KnowledgeQueryEmbeddingCache(options),
            options);

        await service.SearchAsync(new KnowledgeSearchRequest("repeat non-nomic query", Limit: 5), CancellationToken.None).ConfigureAwait(false);
        await service.SearchAsync(new KnowledgeSearchRequest("repeat non-nomic query", Limit: 5), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(1, provider.GenerateCallCount);
        await vectorSearch.Received(2)
                          .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(),
                              nonNomicModel,
                              $"{nonNomicModel}::native:v1:{Dimensions}",
                              Dimensions,
                              Arg.Any<int>(),
                              Arg.Any<Guid?>(),
                              Arg.Any<CancellationToken>())
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task SearchAsync_CachedNativeWidthMismatch_ReembedsAndReplacesEntry()
    {
        var databasePath = GetDatabasePath("search-native-cache-mismatch.sqlite");
        const string query = "poisoned native cache query";
        var options = Options.Create(new KnowledgeBaseOptions
        {
            EmbeddingVectorMode = KnowledgeEmbeddingVectorMode.Native
        });
        var resolution = new EmbeddingModelResolution(ResolvedGgufName, IsConfident: true);
        var cacheFamily = KnowledgeEmbeddingVectorPolicy.CreateCacheFamilyIdentity(resolution, options.Value.EmbeddingVectorMode);
        var cache = new KnowledgeQueryEmbeddingCache(options);
        cache.Store(cacheFamily,
            query,
            new KnowledgeQueryEmbeddingCacheEntry(new float[KnowledgeEmbeddingVectorPolicy.MatryoshkaWidth],
                $"{ResolvedGgufName}::native:v1:{Dimensions}"));
        var provider = new FixedEmbeddingProvider(Descriptor(ResolvedGgufName));
        var vectorSearch = EmptyVectorSearch();
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateSearchService(context, vectorSearch, CreateProviderResolver(provider), cache, options);

        await service.SearchAsync(new KnowledgeSearchRequest(query, Limit: 5), CancellationToken.None).ConfigureAwait(false);
        await service.SearchAsync(new KnowledgeSearchRequest(query, Limit: 5), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(1, provider.GenerateCallCount);
        AssertEx.True(cache.TryGet(cacheFamily, query, out var repaired), "The invalid record must be replaced by the generated vector.");
        AssertEx.Equal($"{ResolvedGgufName}::native:v1:{Dimensions}", repaired.VectorIdentity);
        AssertEx.Equal(Dimensions, repaired.Vector.Length);
    }

    [Test]
    public async Task SearchAsync_CachedNativeIdentityMismatchAtSameWidth_ReembedsAndReplacesEntry()
    {
        var databasePath = GetDatabasePath("search-native-cache-identity-mismatch.sqlite");
        const string query = "wrong native identity query";
        var options = Options.Create(new KnowledgeBaseOptions
        {
            EmbeddingVectorMode = KnowledgeEmbeddingVectorMode.Native
        });
        var resolution = new EmbeddingModelResolution(ResolvedGgufName, IsConfident: true);
        var cacheFamily = KnowledgeEmbeddingVectorPolicy.CreateCacheFamilyIdentity(resolution, options.Value.EmbeddingVectorMode);
        var cache = new KnowledgeQueryEmbeddingCache(options);
        cache.Store(cacheFamily,
            query,
            new KnowledgeQueryEmbeddingCacheEntry(new float[Dimensions],
                $"different-model::native:v1:{Dimensions}"));
        var provider = new FixedEmbeddingProvider(Descriptor(ResolvedGgufName));
        var vectorSearch = EmptyVectorSearch();
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateSearchService(context, vectorSearch, CreateProviderResolver(provider), cache, options);

        await service.SearchAsync(new KnowledgeSearchRequest(query, Limit: 5), CancellationToken.None).ConfigureAwait(false);
        await service.SearchAsync(new KnowledgeSearchRequest(query, Limit: 5), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(1, provider.GenerateCallCount);
        AssertEx.True(cache.TryGet(cacheFamily, query, out var repaired), "The wrong-identity record must be replaced.");
        AssertEx.Equal($"{ResolvedGgufName}::native:v1:{Dimensions}", repaired.VectorIdentity);
        AssertEx.Equal(Dimensions, repaired.Vector.Length);
    }

    [Test]
    public async Task DuringATransientProviderOutage_ResetReturnsEmptyAndListFlagsNoDocumentStale()
    {
        var databasePath = GetDatabasePath("catalog-outage.sqlite");
        var indexedId = Guid.NewGuid();

        await MigrateAsync(databasePath).ConfigureAwait(false);
        // Stored under the RESOLVED GGUF name (the real, pre-outage identity) — NOT the plain configured name a
        // non-confident resolution would fall back to. If the confidence guard were missing, this row would compare
        // unequal to that fallback and get (wrongly) flagged stale and reset during the outage.
        await SeedDocumentAsync(databasePath, indexedId, ResolvedGgufName, KnowledgeDocumentStatus.Indexed).ConfigureAwait(false);

        var options = Options.Create(new KnowledgeBaseOptions());

        IReadOnlyList<Guid> reset;
        IReadOnlyList<KnowledgeDocumentSummary> documents;
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await EnsureForeignKeysOffAsync(context.Database.GetDbConnection()).ConfigureAwait(false);
            var catalogService = new KnowledgeDocumentCatalogService(context,
                CreateOutageProviderResolver(),
                new EmbeddingModelResolver(options),
                options,
                TimeProvider.System);

            reset = await catalogService.ResetStaleDocumentsToPendingAsync(CancellationToken.None).ConfigureAwait(false);
            documents = await catalogService.ListAsync(CancellationToken.None).ConfigureAwait(false);
        }

        AssertEx.Empty(reset, "A non-confident resolution (transient provider outage) must never reset any document, "
                              + "or it would reset the entire indexed corpus during the outage.");

        var indexed = documents.Single(document => document.DocumentId == indexedId);
        AssertEx.False(indexed.StaleModel,
            "A non-confident resolution must never flag a document stale, even though its stored name differs from the fallback.");
        AssertEx.Equal(KnowledgeDocumentStatus.Indexed.ToString(), await ReadStatusAsync(databasePath, indexedId).ConfigureAwait(false));
    }

    // ── service factories ────────────────────────────────────────────────────────────────────────────

    private static KnowledgeIngestionService CreateIngestionService(NodeChatDbContext context)
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

        var extractor = Substitute.For<IDocumentTextExtractor>();
        extractor.ExtractStructuredAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new DocumentStructuredExtractionResult(DocumentExtractionStatus.Extracted, BuildExtractedDocument(), Error: null)));

        return new KnowledgeIngestionService(context,
            blobStore,
            extractor,
            new HeaderBoundaryChunkingService(options),
            CreateEmbedder(options),
            new KnowledgeIndexWriter(context, TimeProvider.System),
            Substitute.For<IKnowledgeIndexingNotifier>(),
            TimeProvider.System,
            NullLogger<KnowledgeIngestionService>.Instance);
    }

    private static KnowledgeDocumentCatalogService CreateCatalogService(NodeChatDbContext context,
        KnowledgeEmbeddingVectorMode vectorMode = KnowledgeEmbeddingVectorMode.Matryoshka512)
    {
        var options = Options.Create(new KnowledgeBaseOptions
        {
            EmbeddingVectorMode = vectorMode
        });
        return new KnowledgeDocumentCatalogService(context, CreateResolvingProviderResolver(), new EmbeddingModelResolver(options), options, TimeProvider.System);
    }

    private static KnowledgeSearchService CreateSearchService(NodeChatDbContext context,
        IVectorSearch vectorSearch,
        ILocalModelProviderResolver? providerResolver = null,
        IKnowledgeQueryEmbeddingCache? queryEmbeddingCache = null,
        IOptions<KnowledgeBaseOptions>? options = null,
        IReadOnlyList<FtsSearchHit>? ftsHits = null)
    {
        options ??= Options.Create(new KnowledgeBaseOptions());

        var ftsSearch = Substitute.For<IFtsSearch>();
        ftsSearch.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(ftsHits ?? (IReadOnlyList<FtsSearchHit>)[]));

        var vectorSearchFactory = Substitute.For<IVectorSearchFactory>();
        vectorSearchFactory.Create().Returns(vectorSearch);

        return new KnowledgeSearchService(context,
            providerResolver ?? CreateResolvingProviderResolver(),
            new EmbeddingModelResolver(options),
            new KnowledgeEmbeddingPrefixer(),
            ftsSearch,
            vectorSearchFactory,
            new ReciprocalRankFusion(),
            Substitute.For<IRerankerClient>(),
            Substitute.For<IContextExpansionService>(),
            queryEmbeddingCache ?? Substitute.For<IKnowledgeQueryEmbeddingCache>(),
            options,
            NullLogger<KnowledgeSearchService>.Instance);
    }

    private static IVectorSearch EmptyVectorSearch()
    {
        var vectorSearch = Substitute.For<IVectorSearch>();
        vectorSearch.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(),
                        Arg.Any<string>(),
                        Arg.Any<string>(),
                        Arg.Any<int>(),
                        Arg.Any<int>(),
                        Arg.Any<Guid?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<VectorSearchHit>>([]));
        return vectorSearch;
    }

    private static KnowledgeChunkEmbedder CreateEmbedder(IOptions<KnowledgeBaseOptions> options)
    {
        return new KnowledgeChunkEmbedder(CreateResolvingProviderResolver(),
            new EmbeddingModelResolver(options),
            new KnowledgeEmbeddingPrefixer(),
            options);
    }

    // A provider resolver whose provider installs the GGUF embedding model (so the resolver picks the GGUF name, which
    // differs from the configured "nomic-embed-text") and generates fixed-dimension non-zero vectors — no Ollama/network.
    private static ILocalModelProviderResolver CreateResolvingProviderResolver()
    {
        var provider = new FixedEmbeddingProvider(Descriptor(ResolvedGgufName), Descriptor("qwen2.5:Q4_K_M"));
        return CreateProviderResolver(provider);
    }

    private static ILocalModelProviderResolver CreateProviderResolver(ILocalModelProvider provider)
    {
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProvider(Arg.Any<string>()).Returns(provider);
        return resolver;
    }

    // A provider resolver whose provider fails to list installed models (a transient outage), so the embedding-model
    // resolver's outcome is NOT confident — the catalog must never treat this fallback as the vectors' real identity.
    private static ILocalModelProviderResolver CreateOutageProviderResolver()
    {
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ListModelsAsync(Arg.Any<CancellationToken>())
                .Returns<Task<IReadOnlyList<LocalModelDescriptor>>>(_ => throw new HttpRequestException("provider down"));

        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProvider(Arg.Any<string>()).Returns(provider);
        return resolver;
    }

    // ── seed + read helpers ──────────────────────────────────────────────────────────────────────────

    private async Task MigrateAsync(string databasePath)
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    private async Task SeedDocumentAsync(string databasePath, Guid documentId, string embeddingModel, KnowledgeDocumentStatus status)
    {
        byte[] encryptedName;
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            encryptedName = context.EncryptKnowledgeFileName("document.txt", documentId);
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await EnsureForeignKeysOffAsync(connection).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO knowledge_documents (document_id, original_file_name, mime_type, extension, size_bytes, content_hash, storage_path,
                                             status, chunk_count, embedding_model, vector_identity, vector_dim, created_at_utc, updated_at_utc)
            VALUES ($id, $name, 'text/plain', '.txt', 10, $hash, $path, $status, 0, $model, $identity, $dim, 1, 1);
            """;
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$name", encryptedName);
        command.Parameters.AddWithValue("$hash", "hash-" + documentId.ToString("N"));
        command.Parameters.AddWithValue("$path", documentId.ToString("D") + ".txt");
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$model", embeddingModel);
        command.Parameters.AddWithValue("$identity",
            status == KnowledgeDocumentStatus.Indexed && string.Equals(embeddingModel, ResolvedGgufName, StringComparison.Ordinal)
                ? ResolvedVectorIdentity
                : KnowledgeEmbeddingVectorPolicy.LegacyIdentity);
        command.Parameters.AddWithValue("$dim",
            status == KnowledgeDocumentStatus.Indexed && string.Equals(embeddingModel, ResolvedGgufName, StringComparison.Ordinal) ? 512 : 0);
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<string> ReadDocumentModelAsync(string databasePath, Guid documentId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT embedding_model FROM knowledge_documents WHERE document_id = $id;";
        command.Parameters.AddWithValue("$id", documentId);
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return (string)result!;
    }

    private static async Task SeedChunkAsync(string databasePath, Guid documentId, Guid chunkId, string content)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await EnsureForeignKeysOffAsync(connection).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO knowledge_document_chunks (chunk_id, document_id, chunk_index, content, token_count)
            VALUES ($chunk, $document, 0, $content, 4);
            """;
        command.Parameters.AddWithValue("$chunk", chunkId);
        command.Parameters.AddWithValue("$document", documentId);
        command.Parameters.AddWithValue("$content", content);
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<string>> ReadVectorModelsAsync(string databasePath, Guid documentId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT embedding_model FROM knowledge_chunk_vectors WHERE document_id = $id;";
        command.Parameters.AddWithValue("$id", documentId);
        var models = new List<string>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            models.Add(reader.GetString(0));
        }

        return models;
    }

    private static async Task<(string Identity, int Dimension)> ReadDocumentVectorIdentityAsync(string databasePath, Guid documentId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT vector_identity, vector_dim FROM knowledge_documents WHERE document_id = $id;";
        command.Parameters.AddWithValue("$id", documentId);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        _ = await reader.ReadAsync().ConfigureAwait(false);
        return (reader.GetString(0), reader.GetInt32(1));
    }

    private static async Task<IReadOnlyList<(string Identity, int Dimension)>> ReadVectorIdentitiesAsync(string databasePath, Guid documentId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT vector_identity, dim FROM knowledge_chunk_vectors WHERE document_id = $id;";
        command.Parameters.AddWithValue("$id", documentId);
        var identities = new List<(string, int)>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            identities.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        return identities;
    }

    private static async Task<string> ReadStatusAsync(string databasePath, Guid documentId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM knowledge_documents WHERE document_id = $id;";
        command.Parameters.AddWithValue("$id", documentId);
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return (string)result!;
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
        section.Elements.Add(new IngestionDocumentParagraph("some indexable body text")
        {
            Text = "some indexable body text"
        });
        document.Sections.Add(section);
        return document;
    }

    private static LocalModelDescriptor Descriptor(string modelName)
    {
        return new LocalModelDescriptor
        {
            ModelName = modelName,
            ProviderName = "llamacpp",
            IsAvailable = true,
            SizeBytes = 1024,
            ModifiedAt = DateTimeOffset.UnixEpoch,
            MaxContextTokens = null,
            Capabilities = []
        };
    }

    // Microsoft.Data.Sqlite enables foreign-key enforcement by default; the node-sqlite runtime connection does not.
    private static async Task EnsureForeignKeysOffAsync(DbConnection connection)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = OFF;";
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    // Node-local provider fake: installs a configurable model set and returns fixed non-zero embedding vectors so the
    // resolver, chunk embedder, and search query embedding all work without Ollama or a network round-trip.
    private sealed class FixedEmbeddingProvider(params LocalModelDescriptor[] models) : ILocalModelProvider
    {
        private int _generateCallCount;

        public string ProviderName => "llamacpp";

        public int GenerateCallCount => Volatile.Read(ref _generateCallCount);

        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LocalModelSelection selection) =>
            new FixedGenerator(this);

        public Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LocalModelDescriptor>>(models);

        public IChatClient CreateChatClient(LocalModelSelection selection) =>
            throw new NotSupportedException();

        public Task<ModelProviderHealth> CheckHealthAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task PullModelAsync(string modelName, IProgress<PullProgress>? progress, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteModelAsync(string modelName, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task WarmModelAsync(string modelName, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task UnloadModelAsync(string modelName, CancellationToken ct) =>
            throw new NotSupportedException();

        private sealed class FixedGenerator(FixedEmbeddingProvider owner) : IEmbeddingGenerator<string, Embedding<float>>
        {
            public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
                EmbeddingGenerationOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner._generateCallCount);
                var embeddings = values.Select(static _ =>
                {
                    var vector = new float[Dimensions];
                    Array.Fill(vector, 0.1f);
                    return new Embedding<float>(vector);
                });
                return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
            }

            public object? GetService(Type serviceType, object? serviceKey = null) =>
                null;

            public void Dispose()
            {
            }
        }
    }
}
