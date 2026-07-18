namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.Memory.Implementation;

internal static class AddNodeAdaptiveMemoryExtensions
{
    public static IHostApplicationBuilder AddNodeAdaptiveMemory(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Extraction model options. Defaults to the node-local chat model so a configured node extracts by default, and
        // so run content is never sent to the cloud chat client by fallback. An empty value (no node-local model
        // configured) disables extraction entirely — the CI-safe gate, mirroring the analysis options + embedding ranker.
        builder.Services.AddOptions<MemoryExtractionOptions>()
               .Bind(builder.Configuration.GetSection(MemoryExtractionOptions.Section))
               .PostConfigure(memoryOptions =>
               {
                   if (string.IsNullOrWhiteSpace(memoryOptions.ExtractionModelName))
                   {
                       memoryOptions.ExtractionModelName = builder.Configuration.GetValue<string>("Ollama:ChatModel")
                                                           ?? builder.Configuration.GetValue<string>("Agent:LocalChat:DefaultModel")
                                                           ?? string.Empty;
                   }

                   // Semantic-dedup threshold must stay in the open-closed cosine interval (0, 1]; a non-positive or >1
                   // value is nonsensical (would drop everything / nothing) so reset to the conservative default.
                   if (memoryOptions.SemanticDedupSimilarityThreshold is <= 0d or > 1d)
                   {
                       memoryOptions.SemanticDedupSimilarityThreshold = 0.92d;
                   }

                   // The RAM-only existing-memory embedding cache bound floors at 1 (mirror the ranker's clamp) so a
                   // misconfigured non-positive value cannot wedge caching once semantic dedup engages.
                   if (memoryOptions.SemanticDedupEmbeddingCacheMaxEntries < 1)
                   {
                       memoryOptions.SemanticDedupEmbeddingCacheMaxEntries = 1;
                   }
               });

        // Extraction agent: mines candidate memories from a completed run using a node-local model only. Singleton
        // because it holds no scoped state and receives a fresh per-run chat client (mirrors the analysis agent).
        builder.Services.AddSingleton<IMemoryExtractionAgent, DefaultMemoryExtractionAgent>();
        // Semantic (embedding-cosine) dedup layer used by the extraction service ON TOP OF its lexical dedup: catches
        // paraphrases the exact normalized-text key misses, gated on a confident node-local embedding model (lexical-only
        // fallback otherwise). Singleton — it holds the long-lived RAM-only existing-memory embedding cache (mirrors the
        // playbook-retrieval ranker). Injected into the scoped extraction service; a singleton is safe there.
        builder.Services.AddSingleton<IMemorySemanticDeduplicator, MemorySemanticDeduplicator>();
        // Extraction orchestration: gates temp chats, no-ops without a model, dedupes (lexical then semantic), and writes
        // Suggested/Extracted actions for human review. Scoped — it consumes the scoped, DbContext-backed playbook store.
        builder.Services.AddScoped<IMemoryExtractionService, MemoryExtractionService>();
        // Background dispatcher + worker: the chat send/regenerate seams call Dispatch once per terminal turn, which
        // TRY-enqueues onto the dispatcher's bounded queue (never blocking the pump; a full queue drops the newest job
        // with a text-free warning). The hosted worker drains that queue under a SemaphoreSlim concurrency gate, runs
        // each job on its own scope/DbContext with the drain-deadline token (so a completed run's memory survives a
        // cancel-after-completion), and awaits in-flight jobs within a bounded window at shutdown. Registered concrete +
        // interface so the worker and the hook share the one queue instance. Replaces the prior unbounded fire-and-forget.
        builder.Services.AddSingleton<MemoryExtractionDispatcher>();
        builder.Services.AddSingleton<IMemoryExtractionDispatcher>(sp => sp.GetRequiredService<MemoryExtractionDispatcher>());
        builder.Services.AddHostedService(sp => new MemoryExtractionWorker(sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<MemoryExtractionDispatcher>(),
            sp.GetRequiredService<IOptions<MemoryExtractionOptions>>(),
            sp.GetRequiredService<ILogger<MemoryExtractionWorker>>()));

        // Execution-log retention policy. The agent_execution_logs telemetry table is append-only, so without a sweep it
        // grows unbounded; AgentExecutionLogRetentionService (registered in the host) reads these to age out old rows.
        builder.Services.AddOptions<AgentExecutionLogRetentionOptions>()
               .Bind(builder.Configuration.GetSection(AgentExecutionLogRetentionOptions.Section));

        return builder;
    }
}
