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
        // Feeds template-detected tool capability INTO the allow-list the gate below reads (see IToolCapableModelRegistrar for why the
        // list is fed, not the gate replaced), so a recommended download is admitted unnamed; the startup backfill corrects older installs.
        builder.Services.AddSingleton<IToolCapableModelRegistrar, ToolCapableModelRegistrar>();
        builder.Services.AddHostedService<ToolCapableModelBackfillService>();
        // LocalToolOfferProvider takes INodeRuntimeSettings itself and reads the migrated AgentHome:ToolCapableModels allow-list LIVE
        // per offer through CachedNodeSettingsStore, whose cache SaveAsync re-primes. Never seed it here: a seed leaves a model saved in Node Settings toolless until restart.
        builder.Services.AddSingleton<ILocalToolOfferProvider>(sp =>
        {
            // Seed the knowledge-tool cloud-locality gate from KnowledgeBase:AllowCloudModelAccess (default false): knowledge tools go
            // to node-local models unless the operator opts a cloud model in. A genuine appsettings knob, not a node setting, so seeding is right.
            var knowledgeOptions = sp.GetRequiredService<IOptions<KnowledgeBaseOptions>>().Value;
            return new LocalToolOfferProvider(sp.GetRequiredService<IAgentToolRegistry>(),
                sp.GetRequiredService<IMcpToolRegistry>(),
                sp.GetRequiredService<INodeRuntimeSettings>(),
                // Singleton provider → the scoped, DbContext-backed custom-tool catalog is resolved per offer from a fresh scope.
                sp.GetRequiredService<IServiceScopeFactory>(),
                // Answers the three locality gates for an external id, which the threaded per-turn cloud flag cannot see: such an id
                // falls THROUGH cloud selection by design, so without it a declared-cloud endpoint would be offered workspace, KB and run_python.
                sp.GetRequiredService<IModelTrustResolver>(),
                knowledgeOptions.AllowCloudModelAccess);
        });
        // The catalog read composed with the node approval policy, which the provider above deliberately never consults.
        // Singleton, like both seams it composes.
        builder.Services.AddSingleton<ToolCatalogService>();
        // The single-named-tool invocation seam, next to the catalog it reads. Registration is UNCONDITIONAL — a feature flag gates
        // behaviour, never registration — so no later caller reasons about another module's flag. Singleton, like every seam it composes.
        builder.Services.AddSingleton<IToolInvocationService, ToolInvocationService>();
        // The always-on tool names a relevance filter may never hide: the work-session state tools plus every approval-bearing built-in
        // from the catalog above. Composed here because both inputs are node-side; the agent assembly consumes only the name set.
        builder.Services.AddSingleton<IToolRelevanceCoreSet, ToolRelevanceCoreSet>();
        // The node's relevance selector: the agent assembly TryAdds the model-free lexical one, and node-side this REPLACES it — never
        // a second AddSingleton, so the winner is not module-order dependent — with the embedding selector, which degrades to the lexical one unless EmbeddingModelName is set.
        builder.Services.AddSingleton<LexicalToolRelevanceSelector>();
        builder.Services.Replace(ServiceDescriptor.Singleton<IToolRelevanceSelector, EmbeddingToolRelevanceSelector>());
        // MCP tool extensibility: the connection manager owns the MCP client lifecycle and republishes the dynamic tool snapshot into
        // the registry offered-tool resolution reads, the startup connector refreshing off the hot path. Singleton: long-lived connections.
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
