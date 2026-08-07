namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Mcp.Runs;

/// <summary>Registers the durable read-only inbound MCP run ledger and its fail-fast lifecycle workers.</summary>
internal static class AddNodeMcpAgentRunsExtensions
{
    public static IHostApplicationBuilder AddNodeMcpAgentRuns(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddOptions<McpAgentRunOptions>()
               .Bind(configuration.GetSection(McpAgentRunOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<McpAgentRunOptions>, McpAgentRunOptionsValidator>();

        // The protector derives process-lifetime domain-separated keys from the node key and clears them on disposal.
        // The EF-backed store is scoped because it owns the scoped NodeChatDbContext.
        builder.Services.AddSingleton<McpAgentRunPayloadProtector>();
        builder.Services.AddScoped<IMcpAgentRunStore, McpAgentRunStore>();

        builder.Services.AddSingleton<McpAgentRunRequestFingerprint>();
        builder.Services.AddSingleton<McpAgentRunCancellationRegistry>();
        builder.Services.AddSingleton<McpAgentRunMetrics>();
        builder.Services.AddScoped<McpAgentRunAccountingService>();
        builder.Services.AddScoped<IMcpAgentRunExecutor, McpAgentRunExecutor>();
        builder.Services.AddScoped<IMcpAgentRunCoordinator, McpAgentRunCoordinator>();

        // Registration order is intentional: accounting/recovery completes in StartAsync before any dispatcher can
        // claim a queued row. Compaction runs independently only after both have started.
        builder.Services.AddHostedService<McpAgentRunRecoveryService>();
        builder.Services.AddHostedService<McpAgentRunDispatcher>();
        builder.Services.AddHostedService<McpAgentRunCompactionService>();

        return builder;
    }
}
