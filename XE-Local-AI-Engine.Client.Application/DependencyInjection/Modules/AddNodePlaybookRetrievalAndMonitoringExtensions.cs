namespace XE_Local_AI_Engine.Client;

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Analysis;
using XE_Local_AI_Engine.Client.Services.Analysis.Implementation;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Auth.Implementation;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Connection.Implementation;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.DeadLetter.Implementation;
using XE_Local_AI_Engine.Client.Services.Eval;
using XE_Local_AI_Engine.Client.Services.Eval.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.Insights;
using XE_Local_AI_Engine.Client.Services.Insights.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage.Implementation;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;
using XE_Local_AI_Engine.Client.Services.Monitoring;
using XE_Local_AI_Engine.Client.Services.Monitoring.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Shutdown;
using XE_Local_AI_Engine.Client.Services.Shutdown.Implementation;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Client.Services.Validation.Implementation;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Ollama;
using ClientSecurityOptions = XE_Local_AI_Engine.Client.Configuration.SecurityOptions;

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
