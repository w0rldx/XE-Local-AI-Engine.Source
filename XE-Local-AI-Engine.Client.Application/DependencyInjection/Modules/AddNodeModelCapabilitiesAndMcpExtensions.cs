namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Client.Services.Proxy;
using XE_Local_AI_Engine.Client.Services.Proxy.Implementation;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Tools;
using XE_Local_AI_Engine.Client.Services.Tools.Implementation;

internal static class AddNodeModelCapabilitiesAndMcpExtensions
{
    public static IHostApplicationBuilder AddNodeModelCapabilitiesAndMcp(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddSingleton<ILocalChatRuntimePackageBuilder, LocalChatRuntimePackageBuilder>();
        // Feeds template-detected tool capability INTO the allow-list the gate below reads, so a model the app itself
        // recommended and downloaded is admitted without the operator hand-typing its name. See
        // IToolCapableModelRegistrar for why the list is fed rather than the gate replaced. The backfill runs at startup
        // so models installed before this existed are corrected too.
        builder.Services.AddSingleton<IToolCapableModelRegistrar, ToolCapableModelRegistrar>();
        builder.Services.AddHostedService<ToolCapableModelBackfillService>();
        // LocalToolOfferProvider takes INodeRuntimeSettings itself and reads the migrated AgentHome:ToolCapableModels
        // allow-list LIVE on each offer (see LocalToolOfferProvider.IsToolCapable). It used to be seeded here once, which
        // meant an operator could add their model in Node Settings, save successfully, and still get no tools until the
        // node restarted — with no restart hint on that field. Do NOT re-introduce the seed: the read goes
        // through CachedNodeSettingsStore (a memory-cache hit that SaveAsync re-primes), and two other consumers of this
        // same setting already read it live per request.
        builder.Services.AddSingleton<ILocalToolOfferProvider>(sp =>
        {
            // Seed the knowledge-tool cloud-locality gate from KnowledgeBase:AllowCloudModelAccess (default false):
            // knowledge tools are offered only to node-local models unless the operator explicitly opts a cloud model in.
            // This one IS a genuine appsettings knob (not a node setting), so seeding it here is correct.
            var knowledgeOptions = sp.GetRequiredService<IOptions<KnowledgeBaseOptions>>().Value;
            return new LocalToolOfferProvider(sp.GetRequiredService<IAgentToolRegistry>(),
                sp.GetRequiredService<IMcpToolRegistry>(),
                sp.GetRequiredService<INodeRuntimeSettings>(),
                // Singleton provider → the scoped, DbContext-backed custom-tool catalog is resolved per offer from a fresh scope.
                sp.GetRequiredService<IServiceScopeFactory>(),
                // Answers the three locality gates for an ext: id, which the threaded per-turn cloud flag cannot see:
                // an external id falls THROUGH cloud selection by design, so without this a declared-cloud endpoint
                // would be offered the workspace, the knowledge base, custom tools and run_python.
                sp.GetRequiredService<IModelTrustResolver>(),
                knowledgeOptions.AllowCloudModelAccess);
        });
        // The single-named-tool invocation seam, next to the catalog it reads. Registration is UNCONDITIONAL: a
        // feature flag gates behaviour, never registration, and this service is feature-neutral — a later caller must
        // not have to reason about whether some other module's flag was on. Singleton, like every seam it composes.
        builder.Services.AddSingleton<IToolInvocationService, ToolInvocationService>();
        // The always-on tool names a relevance filter may never hide: the work-session state tools plus every
        // approval-bearing built-in from the catalog above. Composed here because both inputs are node-side; the agent
        // assembly only ever consumes the resulting name set.
        builder.Services.AddSingleton<IToolRelevanceCoreSet, ToolRelevanceCoreSet>();
        // The node's relevance selector. The agent assembly registers the model-free lexical one with TryAdd; node-side
        // this REPLACES it with the embedding-backed selector, which resolves the concrete lexical one for its own
        // degrade path and only reaches a model when EmbeddingModelName is configured. Replace rather than a second
        // AddSingleton so the winner does not depend on module order. AI.Agent-only tests keep the lexical
        // registration. Both singletons: the vector cache is a long-lived RAM-only store.
        builder.Services.AddSingleton<LexicalToolRelevanceSelector>();
        builder.Services.Replace(ServiceDescriptor.Singleton<IToolRelevanceSelector, EmbeddingToolRelevanceSelector>());
        // MCP tool extensibility. The connection manager owns the MCP client lifecycle and republishes the dynamic
        // tool snapshot into the registry consumed by offered-tool resolution. The startup connector triggers an
        // initial refresh off the hot path; the manager stays singleton because it owns long-lived connections.
        builder.Services.AddOptions<McpOptions>()
               .Bind(configuration.GetSection(McpOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<McpOptions>, McpOptionsValidator>();
        // Scheduler options: controls Quartz activation, concurrency, history retention, and QRTZ table prefix. The
        // hosted service reads Enabled before starting so a disabled scheduler never fires jobs.
        builder.Services.AddOptions<SchedulerOptions>()
               .Bind(configuration.GetSection(SchedulerOptions.Section))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<SchedulerOptions>, SchedulerOptionsValidator>();
        builder.Services.AddSingleton<IMcpClientFactory, McpClientFactory>();
        builder.Services.AddSingleton<IMcpServerConnectionManager, McpServerConnectionManager>();
        builder.Services.AddHostedService<McpServerStartupConnector>();
        // MCP registration service: validates transport fields, loopback URL, and unique names, then republishes the
        // live tool snapshot after enabled-set changes.
        builder.Services.AddScoped<IMcpServerService, McpServerService>();
        // INBOUND direction: the bearer credential an external MCP client presents to this node's own MCP endpoint.
        // Scoped to match the scoped, DbContext-backed key store it reads through.
        builder.Services.AddScoped<IMcpServerApiKeyService, McpServerApiKeyService>();
        // INBOUND model proxy: the bearer credential an external OpenAI-compatible tool presents to this node's
        // raw-model proxy endpoint. Scoped to match its scoped, DbContext-backed key store.
        builder.Services.AddScoped<ILocalModelProxyApiKeyService, LocalModelProxyApiKeyService>();

        return builder;
    }
}
