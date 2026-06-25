namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal static class AddNodeSchedulingStoresExtensions
{
    public static IHostApplicationBuilder AddNodeSchedulingStores(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Scheduler persistence stores. Job definitions, run history, and per-run event payloads are node-local, scoped
        // to the DbContext, and encrypted at rest where the store exposes JSON payload fields.
        builder.Services.AddScoped<IScheduledJobDefinitionStore, ScheduledJobDefinitionStore>();
        builder.Services.AddScoped<IScheduledJobRunStore, ScheduledJobRunStore>();
        builder.Services.AddScoped<IScheduledJobRunEventStore, ScheduledJobRunEventStore>();

        return builder;
    }
}
