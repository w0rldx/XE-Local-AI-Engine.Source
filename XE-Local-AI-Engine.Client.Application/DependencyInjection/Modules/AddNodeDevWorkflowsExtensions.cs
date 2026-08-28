namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

internal static class AddNodeDevWorkflowsExtensions
{
    /// <summary>
    ///     Registers the development-workflow persistence substrate.
    ///     <para>
    ///         As with work sessions, <c>Enabled=false</c> does <em>not</em> skip registration: a disabled node has to
    ///         answer legibly rather than 500 out of an empty container. The switch is enforced in the runtime.
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

        builder.Services.AddScoped<IDevWorkflowStore, DevWorkflowStore>();
        builder.Services.AddSingleton<IDevWorkflowArtifactBlobStore, ManagedDevWorkflowArtifactBlobStore>();
        return builder;
    }
}
