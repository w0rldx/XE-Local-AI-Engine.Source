namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.Memory.Implementation;

internal static class AddNodeAdaptiveMemoryExtensions
{
    /// <summary>
    ///     Registers adaptive-memory extraction: its options, the dedup layers, the extraction service and the
    ///     background dispatcher and worker that run it off the chat pump.
    /// </summary>
    /// <remarks>
    ///     The send and regenerate seams dispatch once per terminal turn, TRY-enqueuing onto a bounded queue that never
    ///     blocks the pump and drops the newest job when full. The hosted worker drains that queue under a concurrency
    ///     gate, runs each job on its own scope and DbContext with the drain-deadline token, so a completed run's memory
    ///     survives a cancel-after-completion, and awaits in-flight jobs within a bounded window at shutdown.
    /// </remarks>
    public static IHostApplicationBuilder AddNodeAdaptiveMemory(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Extraction model options default to the node-local chat model, so a configured node extracts by default and run content
        // never reaches the cloud chat client by fallback; an empty value disables extraction entirely, which is the CI-safe gate.
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
        // Semantic (embedding-cosine) dedup ON TOP OF the extraction service's lexical dedup, catching paraphrases the exact
        // normalized-text key misses; gated on a confident node-local embedding model. Singleton: it holds the RAM-only embedding cache.
        builder.Services.AddSingleton<IMemorySemanticDeduplicator, MemorySemanticDeduplicator>();
        // Extraction orchestration: gates temp chats, no-ops without a model, dedupes (lexical then semantic), and writes
        // Suggested/Extracted actions for human review. Scoped — it consumes the scoped, DbContext-backed playbook store.
        builder.Services.AddScoped<IMemoryExtractionService, MemoryExtractionService>();
        // Background dispatcher + worker, registered concrete AND by interface so the worker and the chat hook share the one queue
        // instance; see this method's remarks for the enqueue, drain and shutdown contract.
        builder.Services.AddSingleton<MemoryExtractionDispatcher>();
        builder.Services.AddSingleton<IMemoryExtractionDispatcher>(sp => sp.GetRequiredService<MemoryExtractionDispatcher>());
        builder.Services.AddHostedService(sp => new MemoryExtractionWorker(sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<MemoryExtractionDispatcher>(),
            sp.GetRequiredService<IOptions<MemoryExtractionOptions>>(),
            sp.GetRequiredService<ILogger<MemoryExtractionWorker>>()));

        // Execution-log retention policy plus its sweeper: agent_execution_logs is append-only and grows unbounded without a sweep.
        // The sweeper resolves the store from a per-sweep scope and registers here, not in the host, since only this layer may reach one.
        builder.Services.AddOptions<AgentExecutionLogRetentionOptions>()
               .Bind(builder.Configuration.GetSection(AgentExecutionLogRetentionOptions.Section));
        builder.Services.AddHostedService<AgentExecutionLogRetentionService>();

        return builder;
    }
}
