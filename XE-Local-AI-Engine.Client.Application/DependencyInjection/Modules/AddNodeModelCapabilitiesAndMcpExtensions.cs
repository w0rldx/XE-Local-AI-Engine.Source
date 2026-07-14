namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Scheduler;

internal static class AddNodeModelCapabilitiesAndMcpExtensions
{
    public static IHostApplicationBuilder AddNodeModelCapabilitiesAndMcp(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddSingleton<ILocalChatRuntimePackageBuilder, LocalChatRuntimePackageBuilder>();
        // LocalToolOfferProvider is a singleton with a synchronous hot offer path, so its tool-capable allow-list is
        // SEEDED once here from INodeRuntimeSettings (migrated AgentHome:ToolCapableModels, stored > seed > default)
        // rather than re-read per offer. The blocking accessor read runs once at singleton construction; a runtime edit
        // applies on the next process restart.
        builder.Services.AddSingleton<ILocalToolOfferProvider>(sp =>
        {
            var runtimeSettings = sp.GetRequiredService<INodeRuntimeSettings>();
            var toolCapableModels = runtimeSettings.GetToolCapableModels();
            // Seed the knowledge-tool cloud-locality gate from KnowledgeBase:AllowCloudModelAccess (default false):
            // knowledge tools are offered only to node-local models unless the operator explicitly opts a cloud model in.
            var knowledgeOptions = sp.GetRequiredService<IOptions<KnowledgeBaseOptions>>().Value;
            return new LocalToolOfferProvider(sp.GetRequiredService<IAgentToolRegistry>(),
                sp.GetRequiredService<IMcpToolRegistry>(),
                toolCapableModels,
                knowledgeOptions.AllowCloudModelAccess);
        });
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

        return builder;
    }
}
