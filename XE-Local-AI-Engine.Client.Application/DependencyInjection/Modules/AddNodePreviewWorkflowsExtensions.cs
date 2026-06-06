namespace XE_Local_AI_Engine.Client;

using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows.Implementation;

/// <summary>
///     Registers the Open Canvas (Preview) workflow application services: CRUD/validation service, the singleton
///     in-memory execution run registry, the idle-TTL sweeper hosted service, and the no-op event publisher default
///     (the Client host swaps in a hub-backed publisher). The Lane B <c>IPreviewWorkflowRunner</c> is registered by the
///     AI.Agent runtime composition root; the canvas store is registered by the workspace/agents module (Lane A).
/// </summary>
internal static class AddNodePreviewWorkflowsExtensions
{
    public static IHostApplicationBuilder AddNodePreviewWorkflows(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        _ = builder.Services.AddOptions<PreviewWorkflowExecutionOptions>()
                   .Bind(configuration.GetSection(PreviewWorkflowExecutionOptions.Section))
                   .ValidateOnStart();

        // CRUD + validation over the encrypted canvas store.
        builder.Services.AddScoped<IPreviewWorkflowService, PreviewWorkflowService>();

        // No-op publisher default — the Client host registers the hub-backed publisher that supersedes it.
        builder.Services.AddSingleton<IPreviewWorkflowEventPublisher, NullPreviewWorkflowEventPublisher>();

        // Singleton in-memory run registry. Registered as the concrete type AND the interface so the idle sweeper and
        // the hub-disconnect path resolve the SAME instance the interface consumers use.
        builder.Services.AddSingleton<PreviewWorkflowExecutionService>();
        builder.Services.AddSingleton<IPreviewWorkflowExecutionService>(sp => sp.GetRequiredService<PreviewWorkflowExecutionService>());

        // Idle-TTL + wall-clock sweeper. Paused runs are exempt (idle clock suspended).
        builder.Services.AddHostedService<PreviewWorkflowIdleSweeper>();

        return builder;
    }
}
