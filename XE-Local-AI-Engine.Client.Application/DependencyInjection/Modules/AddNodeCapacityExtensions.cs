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
    public static IHostApplicationBuilder AddNodeCapacity(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // The capacity gate for sub-agent spawns. The footprint provider is stateless (it wraps the singleton
        // MemoryFitEstimator over the GGUF store's own header-facts cache) → Singleton. The pending-footprint ledger
        // owns the process-wide decide-commit gate and the in-flight reservation total, which MUST survive across the
        // per-spawn DI scopes the capacity service is resolved in → Singleton. CapacityService itself depends only on
        // singletons but is Scoped: the server-side spawn tool body resolves it through a fresh DI scope per spawn,
        // mirroring how the model-routing path consumes the scoped resolver. MemoryFitEstimator is registered by
        // AddNodeModelFit; this module runs after it.
        builder.Services.AddSingleton<IModelFootprintProvider, ModelFootprintProvider>();
        builder.Services.AddSingleton<IProcessContextAllocationResolver, ProcessContextAllocationResolver>();
        builder.Services.AddSingleton<IProcessLaunchAdmissionRegistry, ProcessLaunchAdmissionRegistry>();
        builder.Services.AddSingleton<IPendingFootprintLedger, PendingFootprintLedger>();
        builder.Services.AddScoped<ICapacityService, CapacityService>();

        // Sub-agent spawn. The SpawnOptions bound the per-root fan-out / cloud-spawn caps and the bounded
        // same-model queue wait. The SpawnQueue owns the process-wide per-(model,role) serialization map → Singleton
        // (it must be shared across every concurrent spawn). SubAgentSpawnService is Scoped: the spawn tool body
        // resolves it through a fresh DI scope per call (it depends on the scoped IChatClient pipeline + the scoped
        // capacity service). The same scoped implementation is exposed through two deliberately separate interfaces:
        // trusted in-process sub-agent orchestration and stricter unattended inbound MCP execution. The spawn tool
        // handler is a Singleton IClientLocalToolHandler (ClientLocalToolRegistry captures the handler IEnumerable at
        // construction, so a scoped handler would be a captive dependency); it resolves the scoped spawn service from
        // a fresh scope per invocation.
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
