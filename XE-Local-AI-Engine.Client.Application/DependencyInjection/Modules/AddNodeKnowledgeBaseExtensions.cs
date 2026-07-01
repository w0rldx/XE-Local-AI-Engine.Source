namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Services.Knowledge;

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
        // Depends only on singletons (provider resolver, prefixer, options); disposes each generator per call.
        builder.Services.AddSingleton<IKnowledgeChunkEmbedder, KnowledgeChunkEmbedder>();

        // Scoped: these use the scoped NodeChatDbContext and are resolved inside the per-ingestion-job scope.
        builder.Services.AddScoped<IKnowledgeIndexWriter, KnowledgeIndexWriter>();
        builder.Services.AddScoped<IKnowledgeIngestionService, KnowledgeIngestionService>();

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

        return builder;
    }
}
