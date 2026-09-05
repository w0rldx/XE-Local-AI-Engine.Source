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

        // Scoped, like every store that reaches the DbContext. No publishing decorator in this slice: S0 has nothing to
        // announce — the definition half writes no run anybody is watching. S1 wraps this registration when it does.
        builder.Services.AddScoped<IGraphWorkflowStore, GraphWorkflowStore>();

        // The one write seam. Scoped because the store it drives is, and because a validation answer is per-request.
        builder.Services.AddScoped<IGraphWorkflowDefinitionService, GraphWorkflowDefinitionService>();

        // The run command surface, scoped for the same reason.
        builder.Services.AddScoped<IGraphWorkflowRunService, GraphWorkflowRunService>();

        // TryAdd, so the dispatcher's own registration wins the moment that slice lands. Until then a started run
        // commits and sits Pending, which is what a node with no tick loop honestly looks like.
        builder.Services.TryAddSingleton<IGraphWorkflowDispatcherSignal, NoOpGraphWorkflowDispatcherSignal>();

        return builder;
    }
}
