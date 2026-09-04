namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

internal static class AddNodeDevWorkflowsExtensions
{
    /// <summary>
    ///     Registers the development-workflow persistence substrate and its runtime.
    ///     <para>
    ///         As with work sessions, <c>Enabled=false</c> does <em>not</em> skip registration: a disabled node has to
    ///         answer legibly rather than 500 out of an empty container. The switch is enforced in the runtime — the
    ///         dispatcher registers either way and simply never starts its loop.
    ///     </para>
    /// </summary>
    public static IHostApplicationBuilder AddNodeDevWorkflows(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddOptions<DevWorkflowOptions>()
               .Bind(configuration.GetSection(DevWorkflowOptions.Section))
               .ValidateDataAnnotations()
               .ValidateOnStart();

        // Cross-section: an agent node is a work session, and neither section's data annotations can see the other.
        builder.Services.AddSingleton<IValidateOptions<DevWorkflowOptions>, DevWorkflowOptionsValidator>();

        // The store is resolved through the publishing decorator, so no caller can commit a change without announcing
        // it. Registering the concrete type separately is what lets the decorator take it as its inner store.
        builder.Services.AddScoped<DevWorkflowStore>();

        // Scoped: it reads the work-session and execution-log stores, which are scoped. The decorator below resolves
        // it from a scope IT opens per collection rather than from the tick's scope, because a collection that
        // overruns its deadline is abandoned and must not still be reading on the DbContext the settle then writes on.
        builder.Services.AddScoped<IDevWorkflowNodeTelemetrySource, DevWorkflowNodeTelemetrySource>();
        builder.Services.AddScoped<IDevWorkflowStore>(services => new PublishingDevWorkflowStore(services.GetRequiredService<DevWorkflowStore>(),
            services.GetRequiredService<IDevWorkflowEventPublisher>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<DevWorkflowGraphCache>(),
            services.GetRequiredService<ILogger<PublishingDevWorkflowStore>>()));
        builder.Services.TryAddSingleton<IDevWorkflowEventPublisher, NoOpDevWorkflowEventPublisher>();
        builder.Services.AddSingleton<IDevWorkflowArtifactBlobStore, ManagedDevWorkflowArtifactBlobStore>();

        // One entry per live run, replaced when the run's graph revision moves. A singleton because the dispatcher is.
        builder.Services.AddSingleton<DevWorkflowGraphCache>();

        // A singleton because both lanes and the dispatcher have to agree about one run's re-attempts, and because the
        // delay a node asks for before trying again outlives the tick that scheduled it.
        builder.Services.AddSingleton<DevWorkflowRetryPolicy>();

        // A singleton for the same reason, and because it holds nothing between ticks: everything it reads it is
        // handed, and everything it writes goes through the store inside the tick's own serialization.
        builder.Services.AddSingleton<DevWorkflowMaterializer>();

        // Scoped: both reach the run store and the work-session family through scoped stores, and the dispatcher
        // resolves them inside the per-tick scope it already opens.
        builder.Services.AddScoped<DevWorkflowArtifactPromotion>();
        builder.Services.AddScoped<DevWorkflowAgentExecutor>();

        // Scoped like the agent lane and for the same reason, plus one of its own: the Development services it drives
        // are scoped and are not registered at all when Development Mode is off, so it asks the scope for them and
        // answers a node run legibly when they are absent.
        builder.Services.AddScoped<DevWorkflowDevTaskExecutor>();
        builder.Services.AddScoped<IDevWorkflowRunService, DevWorkflowRunService>();

        // A singleton, unlike the agent executor: the sandbox lane's slot count and its in-flight registry outlive a
        // tick and a scope, and a second instance would hand the same slots out twice.
        builder.Services.AddSingleton<DevWorkflowToolExecutor>();

        // Only when Development Mode is on, because that is where the workspace provider, the repository bindings and
        // the sandbox come from. A tool node on a node with it switched off then finds no commands to run and says so,
        // which is a configuration answer rather than a container failure deep inside a detached task.
        if (configuration.GetValue($"{DevelopmentOptions.Section}:Enabled", defaultValue: true))
        {
            builder.Services.AddScoped<IDevWorkflowToolCommands, DevWorkflowToolCommands>();

            // The integration variant of the same lane, registered under the same condition and for a stronger reason:
            // what it drives IS Dev Mode's apply gate, so with Development Mode off there is nothing for it to call.
            // The lane then answers such a node run with a configuration reason, as it does a validation node's.
            builder.Services.AddScoped<DevWorkflowApplyCommands>();
        }

        // Before both, and independent of both: a template has to exist before anyone can start a run from it, and
        // seeding one neither reconciles anything nor needs a loop.
        builder.Services.AddHostedService<DevWorkflowDefinitionSeeder>();

        // BEFORE the dispatcher, and this module is added after work sessions: a node run that resumes must not find
        // its session still holding a half-written turn, and the dispatcher must not admit rows this has not judged.
        builder.Services.AddHostedService<DevWorkflowStartupReconciler>();

        // One instance serving three roles, the same pairing the work-session supervisor uses: a second instance would
        // hold its own signal channel, so half the signals would reach a loop that is not the one advancing runs.
        builder.Services.AddSingleton<DevWorkflowDispatcher>();
        builder.Services.AddSingleton<IDevWorkflowDispatcherSignal>(services => services.GetRequiredService<DevWorkflowDispatcher>());
        builder.Services.AddSingleton<IHostedService>(services => services.GetRequiredService<DevWorkflowDispatcher>());
        return builder;
    }
}
