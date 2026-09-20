namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

internal static class AddNodeCapacityExtensions
{
    /// <summary>
    ///     Registers the capacity gate and the sub-agent spawn path the server-side spawn tool runs through.
    /// </summary>
    /// <remarks>
    ///     Lifetimes are load-bearing. <c>IPendingFootprintLedger</c> and <c>ISpawnSerializer</c> are Singletons: they own
    ///     the process-wide decide-commit gate, the in-flight reservation total and the per-(model,role) serialization map,
    ///     which must outlive the per-spawn DI scopes. <c>ICapacityService</c> and <c>SubAgentSpawnService</c> are Scoped
    ///     because each spawn tool body resolves them from a fresh scope, while the spawn handler is a Singleton, since
    ///     <c>ClientLocalToolRegistry</c> captures its handlers at construction and a Scoped handler would be captive.
    /// </remarks>
    public static IHostApplicationBuilder AddNodeCapacity(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // The capacity gate for sub-agent spawns. The stateless footprint provider wraps MemoryFitEstimator over the GGUF store's
        // header-facts cache; that estimator is registered by AddNodeModelFit, which runs before this module.
        builder.Services.AddSingleton<IModelFootprintProvider, ModelFootprintProvider>();
        builder.Services.AddSingleton<IProcessContextAllocationResolver, ProcessContextAllocationResolver>();
        builder.Services.AddSingleton<IProcessLaunchAdmissionRegistry, ProcessLaunchAdmissionRegistry>();
        builder.Services.AddSingleton<IPendingFootprintLedger, PendingFootprintLedger>();
        builder.Services.AddScoped<ICapacityService, CapacityService>();

        // Sub-agent spawn: SpawnOptions bound the per-root fan-out and cloud-spawn caps and the bounded same-model queue wait.
        // One Scoped implementation serves two deliberately separate interfaces — trusted in-process orchestration, and stricter unattended inbound MCP execution.
        builder.Services.AddOptions<SpawnOptions>()
               .Bind(configuration.GetSection(SpawnOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<SpawnOptions>, SpawnOptionsValidator>();

        builder.Services.AddSingleton<ISpawnSerializer, SpawnSerializer>();
        builder.Services.AddScoped<IMcpWorkspaceExecutionSessionFactory, McpWorkspaceExecutionSessionFactory>();
        builder.Services.AddScoped<IMcpExecutionBindingResolver, McpExecutionBindingResolver>();
        builder.Services.AddScoped<IMcpAgenticApprovalAuditRecorder, McpAgenticApprovalAuditRecorder>();
        builder.Services.AddScoped<IMcpAgenticToolAdapter, McpAgenticToolAdapter>();
        builder.Services.AddScoped<SubAgentSpawnService>();
        builder.Services.AddScoped<ISubAgentSpawnService>(static services => services.GetRequiredService<SubAgentSpawnService>());
        builder.Services.AddScoped<IMcpAgentExecutionService>(static services => services.GetRequiredService<SubAgentSpawnService>());
        builder.Services.AddSingleton<IClientLocalToolHandler, SpawnSubAgentToolHandler>();

        return builder;
    }
}
