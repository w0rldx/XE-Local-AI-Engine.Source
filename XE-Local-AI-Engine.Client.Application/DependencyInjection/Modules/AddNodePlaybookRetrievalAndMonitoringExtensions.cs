namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Monitoring;
using XE_Local_AI_Engine.Client.Services.Monitoring.Implementation;

internal static class AddNodePlaybookRetrievalAndMonitoringExtensions
{
    public static IHostApplicationBuilder AddNodePlaybookRetrievalAndMonitoring(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Playbook relevance-retrieval ranker: the resolver/orchestration paths consult it only when an agent's
        // Enabled set exceeds the retrieval threshold and the send carries a non-blank query; below that the full static
        // prepend is used (byte-identical). The lexical ranker (deterministic, model-free, stateless) is registered
        // concretely as the fallback/disabled path; the embedding ranker is the IPlaybookRetrievalRanker — it resolves
        // the concrete lexical for graceful degradation and ranks via the node-local embedding model only when
        // EmbeddingModelName is configured. Both are Singletons (the embedding cache is a long-lived RAM-only store).
        builder.Services.AddSingleton<LexicalPlaybookRetrievalRanker>();
        builder.Services.AddSingleton<IPlaybookRetrievalRanker, EmbeddingPlaybookRetrievalRanker>();
        builder.Services.AddOptions<PlaybookRetrievalOptions>()
               .Bind(builder.Configuration.GetSection(PlaybookRetrievalOptions.Section))
               .PostConfigure(static retrievalOptions =>
               {
                   // Guard against config that would disable the gate nonsensically: a non-positive top-k or threshold
                   // is clamped to the defaults so retrieval, once engaged, always injects at least one action.
                   if (retrievalOptions.RetrievalThreshold < 0)
                   {
                       retrievalOptions.RetrievalThreshold = 0;
                   }

                   if (retrievalOptions.TopK < 1)
                   {
                       retrievalOptions.TopK = 1;
                   }

                   // The embedding cache bound floors at 1 (mirror the MaxEnabledActions clamp) so a misconfigured
                   // non-positive value cannot wedge candidate caching once the embedding ranker engages.
                   if (retrievalOptions.EmbeddingCacheMaxEntries < 1)
                   {
                       retrievalOptions.EmbeddingCacheMaxEntries = 1;
                   }
               });
        // Bounded playbook-store options: floor the enabled-action cap at 1 so a misconfigured non-positive value cannot
        // wedge every promote/manual-enable.
        builder.Services.AddOptions<PlaybookActionOptions>()
               .Bind(builder.Configuration.GetSection(PlaybookActionOptions.Section))
               .PostConfigure(static actionOptions =>
               {
                   if (actionOptions.MaxEnabledActions < 1)
                   {
                       actionOptions.MaxEnabledActions = 1;
                   }
               });
        // Cohort-monitor read store: windowed feedback counts over node-local message_feedback/tool-event rows. Pure
        // analytics, computed on read, and writes nothing.
        builder.Services.AddScoped<IPlaybookMonitorStore, PlaybookMonitorStore>();
        // Cohort-monitor service: classifies enabled actions against the epsilon/sample floor and flags flat/regressed
        // actions for human review. It never auto-disables actions and runs only from the monitor endpoint.
        builder.Services.AddScoped<IPlaybookMonitorService, PlaybookMonitorService>();
        builder.Services.AddOptions<PlaybookMonitorOptions>()
               .Bind(builder.Configuration.GetSection(PlaybookMonitorOptions.Section))
               .PostConfigure(static monitorOptions =>
               {
                   // Clamp to safe bounds: a negative epsilon would invert the dead-band, a non-positive sample floor
                   // would let a single after-enable vote draw a verdict (and a flag).
                   if (monitorOptions.ImprovementEpsilon < 0d)
                   {
                       monitorOptions.ImprovementEpsilon = 0d;
                   }

                   if (monitorOptions.MinSampleSize < 1)
                   {
                       monitorOptions.MinSampleSize = 1;
                   }
               });

        return builder;
    }
}
