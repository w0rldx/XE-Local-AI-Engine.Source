namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

internal static class AddNodeGraphWorkflowsExtensions
{
    /// <summary>
    ///     Registers the graph-workflow configuration.
    ///     <para>
    ///         As with development workflows, <c>Enabled=false</c> does <em>not</em> skip registration: a disabled node
    ///         has to answer legibly rather than 500 out of an empty container. The switch is enforced by the
    ///         request-path gate in <c>Program</c> and, later, by the runtime this module grows.
    ///     </para>
    ///     <para>
    ///         The store, the definition service and the dispatcher are registered here as the slice adds them; the
    ///         options binding is what every one of them resolves.
    ///     </para>
    /// </summary>
    public static IHostApplicationBuilder AddNodeGraphWorkflows(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddOptions<GraphWorkflowOptions>()
               .Bind(configuration.GetSection(GraphWorkflowOptions.Section))
               .ValidateDataAnnotations()
               .ValidateOnStart();

        // The floors under the budgets and the one relation between two of them, neither of which a data annotation
        // can express. Failing them at startup beats meeting them once per node run.
        builder.Services.AddSingleton<IValidateOptions<GraphWorkflowOptions>, GraphWorkflowOptionsValidator>();

        // Scoped, like every store that reaches the DbContext. The store is resolved through the publishing decorator,
        // so no caller can commit a run change without announcing it; registering the concrete type separately is what
        // lets the decorator take it as its inner store.
        builder.Services.AddScoped<GraphWorkflowStore>();
        builder.Services.AddScoped<IGraphWorkflowStore>(services => new PublishingGraphWorkflowStore(services.GetRequiredService<GraphWorkflowStore>(),
            services.GetRequiredService<IGraphWorkflowEventPublisher>(),
            services.GetRequiredService<ILogger<PublishingGraphWorkflowStore>>()));

        // TryAdd, so the hub-backed publisher in the Client host wins wherever the hub is present. Without it a host
        // that maps no hub still resolves the store.
        builder.Services.TryAddSingleton<IGraphWorkflowEventPublisher, NoOpGraphWorkflowEventPublisher>();

        // The one write seam. Scoped because the store it drives is, and because a validation answer is per-request.
        builder.Services.AddScoped<IGraphWorkflowDefinitionService, GraphWorkflowDefinitionService>();

        // The run command surface, scoped for the same reason.
        builder.Services.AddScoped<IGraphWorkflowRunService, GraphWorkflowRunService>();

        // The five kinds that run inside the tick. A singleton because it holds nothing per run — only the output cap.
        builder.Services.AddSingleton<GraphWorkflowInlineExecutor>();

        // The Agent lane. Registered as one of the executor SET the dispatcher asks which kind it owns, so adding a
        // lane is a registration and nothing else: the tick's dispatch switch has no per-kind arm to grow.
        builder.Services.AddSingleton<IGraphWorkflowNodeExecutor, GraphWorkflowAgentExecutor>();

        // BEFORE the dispatcher: hosted services start in registration order, and the dispatcher's pumps must not begin
        // admitting node runs a restart has not judged yet.
        builder.Services.AddHostedService<GraphWorkflowStartupReconciler>();

        // One instance under three service types: the loop, the signal every command path calls after its commit, and
        // the hosted service that starts the two pumps. Its own DisposeAsync is idempotent, because the container
        // tracks each factory registration's result for disposal separately.
        builder.Services.AddSingleton<GraphWorkflowDispatcher>();
        builder.Services.AddSingleton<IGraphWorkflowDispatcherSignal>(services => services.GetRequiredService<GraphWorkflowDispatcher>());
        builder.Services.AddSingleton<IHostedService>(services => services.GetRequiredService<GraphWorkflowDispatcher>());

        return builder;
    }
}
