namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

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
               });

        // Extraction agent: mines candidate memories from a completed run using a node-local model only. Singleton
        // because it holds no scoped state and receives a fresh per-run chat client (mirrors the analysis agent).
        builder.Services.AddSingleton<IMemoryExtractionAgent, OllamaMemoryExtractionAgent>();
        // Extraction orchestration: gates temp chats, no-ops without a model, dedupes, and writes Suggested/Extracted
        // actions for human review. Scoped — it consumes the scoped, DbContext-backed playbook action store.
        builder.Services.AddScoped<IMemoryExtractionService, MemoryExtractionService>();
        // Background dispatcher: fire-and-forget post-run hook the chat send/regenerate seams call once. Singleton — it
        // owns the scope factory and spins each job onto its own scope/DbContext with a fresh cancellation token, so the
        // chat hot path is never blocked and a completed run's memory survives a cancel-after-completion.
        builder.Services.AddSingleton<IMemoryExtractionDispatcher, MemoryExtractionDispatcher>();

        // Execution-log retention policy. The agent_execution_logs telemetry table is append-only, so without a sweep it
        // grows unbounded; AgentExecutionLogRetentionService (registered in the host) reads these to age out old rows.
        builder.Services.AddOptions<AgentExecutionLogRetentionOptions>()
               .Bind(builder.Configuration.GetSection(AgentExecutionLogRetentionOptions.Section));

        return builder;
    }
}
