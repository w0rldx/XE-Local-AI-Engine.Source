namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

internal static class AddNodeWorkSessionsExtensions
{
    /// <summary>
    ///     Registers the work-session persistence substrate.
    ///     <para>
    ///         Unlike the Development module, <c>Enabled=false</c> does <em>not</em> skip registration: the REST surface
    ///         and the hub are mapped unconditionally, so an empty container would answer 500 where a disabled node has
    ///         to answer legibly. The switch is enforced in the reconciler and, later, in the runtime.
    ///     </para>
    /// </summary>
    public static IHostApplicationBuilder AddNodeWorkSessions(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddOptions<WorkSessionOptions>()
               .Bind(configuration.GetSection(WorkSessionOptions.Section))
               .ValidateDataAnnotations()
               .ValidateOnStart();

        builder.Services.AddScoped<IAgentWorkSessionStore, AgentWorkSessionStore>();
        builder.Services.AddSingleton<IWorkSessionArtifactBlobStore, ManagedWorkSessionArtifactBlobStore>();
        builder.Services.AddHostedService<WorkSessionStartupReconciler>();
        return builder;
    }
}
