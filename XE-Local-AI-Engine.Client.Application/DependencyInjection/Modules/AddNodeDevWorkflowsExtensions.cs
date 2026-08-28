namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
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

        builder.Services.AddScoped<IDevWorkflowStore, DevWorkflowStore>();
        builder.Services.AddSingleton<IDevWorkflowArtifactBlobStore, ManagedDevWorkflowArtifactBlobStore>();

        // One entry per live run, replaced when the run's graph revision moves. A singleton because the dispatcher is.
        builder.Services.AddSingleton<DevWorkflowGraphCache>();

        // Scoped: both reach the run store and the work-session family through scoped stores, and the dispatcher
        // resolves them inside the per-tick scope it already opens.
        builder.Services.AddScoped<DevWorkflowArtifactPromotion>();
        builder.Services.AddScoped<DevWorkflowAgentExecutor>();

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
