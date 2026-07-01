namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Tools.Implementation;

internal static class AddNodeKnowledgeBaseExtensions
{
    public static IHostApplicationBuilder AddNodeKnowledgeBase(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Durable knowledge-base document store. Singleton: it opens its own DbContext scope per operation and depends
        // only on singletons (data directory, sqlite key holder, time provider), mirroring the conversation uploaded-file
        // store, so it can be injected into the singleton ingestion/cleanup surfaces that reach it.
        builder.Services.AddSingleton<IKnowledgeDocumentBlobStore, KnowledgeDocumentBlobStore>();

        // Ingestion + embedding options (section "KnowledgeBase").
        _ = builder.Services.AddOptions<KnowledgeBaseOptions>()
                   .Bind(configuration.GetSection(KnowledgeBaseOptions.Section))
                   .ValidateOnStart();

        // Stateless, thread-safe collaborators — safe as singletons and injectable into the scoped ingestion service.
        builder.Services.AddSingleton<IChunkingService, HeaderBoundaryChunkingService>();
        builder.Services.AddSingleton<IKnowledgeEmbeddingPrefixer, KnowledgeEmbeddingPrefixer>();
        // Maps the configured embedding name to a model actually installed on the resolved provider (shared by the
        // ingestion embedder and the search lane so chunk + query vectors use the identical model). Options-only → singleton.
        builder.Services.AddSingleton<IEmbeddingModelResolver, EmbeddingModelResolver>();
        // Depends only on singletons (provider resolver, embedding-model resolver, prefixer, options); disposes each generator per call.
        builder.Services.AddSingleton<IKnowledgeChunkEmbedder, KnowledgeChunkEmbedder>();

        // No-op indexing notifier default so the ingestion service always resolves one (Application-only/test hosts). The
        // Client host supersedes this with a hub-backed notifier that pushes status changes over SignalR.
        builder.Services.AddSingleton<IKnowledgeIndexingNotifier, NullKnowledgeIndexingNotifier>();

        // Scoped: these use the scoped NodeChatDbContext and are resolved inside the per-ingestion-job scope.
        builder.Services.AddScoped<IKnowledgeIndexWriter, KnowledgeIndexWriter>();
        builder.Services.AddScoped<IKnowledgeIngestionService, KnowledgeIngestionService>();

        // Scoped management surfaces for the Lane-D endpoints: the delete purge (explicit ordered raw-SQL deletes, since
        // FK cascade is OFF) and the read + reindex-reset catalog. Both drive the request-scoped NodeChatDbContext.
        builder.Services.AddScoped<IKnowledgeDocumentPurgeService, KnowledgeDocumentPurgeService>();
        builder.Services.AddScoped<IKnowledgeDocumentCatalogService, KnowledgeDocumentCatalogService>();

        // Reciprocal Rank Fusion is a pure, stateless function over rank lists — safe as a singleton, no DbContext.
        builder.Services.AddSingleton<IRankingFusionService, ReciprocalRankFusion>();

        // Search lane (Lane C). SCOPED (M3): each retrieval collaborator reads through the request-scoped NodeChatDbContext
        // connection, so all are resolved inside the per-search scope. The vector backend is selected via the
        // scoped-resolving IVectorSearchFactory — NOT a singleton keyed registration that would capture a scoped DbContext.
        builder.Services.AddScoped<IFtsSearch, FtsSearch>();
        builder.Services.AddScoped<IVectorSearch, ManagedCosineVectorSearch>();
        builder.Services.AddScoped<IVectorSearchFactory, VectorSearchFactory>();
        builder.Services.AddScoped<IContextExpansionService, ContextExpansionService>();
        builder.Services.AddScoped<IKnowledgeSearchService, KnowledgeSearchService>();

        // Singleton queue seam the upload endpoint calls; registered as the concrete type AND the interface so the worker
        // drains the SAME instance the endpoint enqueues onto.
        builder.Services.AddSingleton<KnowledgeIngestionDispatcher>();
        builder.Services.AddSingleton<IKnowledgeIngestionDispatcher>(sp => sp.GetRequiredService<KnowledgeIngestionDispatcher>());

        // Background worker: drains the queue with SemaphoreSlim-bounded concurrency, a fresh scope per document.
        builder.Services.AddHostedService<KnowledgeIngestionWorker>();

        // Read-only knowledge-base agent tools (search_knowledge_base / read_document / read_surrounding_chunks). All
        // Singleton: ClientLocalToolRegistry captures the IClientLocalToolHandler IEnumerable at construction, so a
        // scoped handler would be a captive dependency; each resolves its scoped retrieval service from a fresh scope
        // per call. They are gated by KnowledgeBase:AgentToolsEnabled (default true) and merged into the capability-gated
        // loopback offer by LocalToolOfferProvider.
        builder.Services.AddSingleton<IClientLocalToolHandler, SearchKnowledgeBaseToolHandler>();
        builder.Services.AddSingleton<IClientLocalToolHandler, ReadDocumentToolHandler>();
        builder.Services.AddSingleton<IClientLocalToolHandler, ReadSurroundingChunksToolHandler>();

        return builder;
    }
}
