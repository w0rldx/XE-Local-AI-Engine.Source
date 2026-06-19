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
using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
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
using XE_Local_AI_Engine.Providers.Capabilities;
using XE_Local_AI_Engine.Providers.Ollama;
using ClientSecurityOptions = XE_Local_AI_Engine.Client.Configuration.SecurityOptions;

internal static class AddNodeModelFitExtensions
{
    public static IHostApplicationBuilder AddNodeModelFit(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Model-fit persistence stores. Snapshots carry sanitized-by-default summaries; the encrypted raw output, stderr
        // and diagnostics are exposed only on the explicit operator-diagnostics read. Recommendation and benchmark rows
        // are normalized snapshot projections. Scoped to match the scoped, DbContext-backed stores.
        // The approved-image registry store is no longer registered — the approved-image concept (its query-
        // service read + the list endpoint) was removed. The orphaned table/entity is left in place (no
        // destructive migration); nothing writes or reads it.
        builder.Services.AddScoped<IModelFitSnapshotStore, ModelFitSnapshotStore>();
        builder.Services.AddScoped<IModelFitRecommendationStore, ModelFitRecommendationStore>();
        builder.Services.AddScoped<IModelFitBenchmarkStore, ModelFitBenchmarkStore>();
        // The request validator allowlists the recommend intent params (use-case + limit bounds). Stateless → singleton.
        builder.Services.AddSingleton<ModelFitRequestValidator>();
        // The memory-fit estimator is a pure, stateless function over GGUF header metadata + the hardware
        // profile → singleton. Consumed by the advisor to score each candidate GGUF file's fit.
        builder.Services.AddSingleton<MemoryFitEstimator>();
        // Model-fit refresh service = the local model advisor: the single non-bypass path that profiles hardware,
        // discovers candidate GGUF files (the Hugging Face GGUF store), estimates memory fit, ranks the survivors and
        // replaces the cached recommendation snapshot. Invoked only by the scheduler's ModelRecommendationCheckHandler.
        // Scoped because it composes the Scoped DbContext-backed snapshot/recommendation stores (the hardware-profiler
        // and GGUF-store seams it depends on are singletons).
        builder.Services.AddScoped<IModelFitRefreshService, ModelFitRefreshService>();
        // The operator-driven GGUF download coordinator owns a per-model cancellation registry so a download
        // started by one HTTP request can be cancelled by a separate request, and tracks the latest sanitized progress.
        // Singleton because the download runs detached after the request scope that started it has returned (it composes
        // the singleton Hugging Face GGUF store IGgufModelStore). The advisor management endpoints (download/cancel)
        // consume it.
        builder.Services.AddSingleton<IGgufDownloadCoordinator, GgufDownloadCoordinator>();
        // Model-fit local-API services. The query service is a pure cache reader over the persistence stores (sanitized
        // snapshot summary + normalized recommendation rows) and takes NO dependency on the runner or refresh service, so
        // a read can never start an advisor run. The refresh trigger is a template-guarded facade over
        // the scheduler trigger service: it fires only an existing model-recommendation-check definition and never runs
        // the advisor itself. Both are Scoped because they compose the Scoped, DbContext-backed stores / scheduler service.
        builder.Services.AddScoped<IModelFitQueryService, ModelFitQueryService>();
        builder.Services.AddScoped<IModelFitRefreshTrigger, ModelFitRefreshTrigger>();

        // The cross-platform hardware profiler (RAM/VRAM/GPU-vendor/CPU/free-disk), extracted out of the removed
        // HostAgent.Linux CapabilityDetector into the surviving Providers.Capabilities project so it compiles with ZERO
        // HostAgent.* references. Singleton — the profile is cached in-memory and re-probed only on forceRefresh:true.
        // The free-disk figure is reported for the models volume, resolved here from the host content root (no
        // Linux-specific default leaks in, unlike the old detector).
        builder.Services.AddHardwareProfiler(builder.Environment.ContentRootPath);

        return builder;
    }
}
