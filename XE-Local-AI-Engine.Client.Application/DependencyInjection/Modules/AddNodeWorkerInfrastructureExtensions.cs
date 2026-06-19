namespace XE_Local_AI_Engine.Client;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.DeadLetter.Implementation;
using XE_Local_AI_Engine.Client.Services.Shutdown;
using XE_Local_AI_Engine.Client.Services.Shutdown.Implementation;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;

internal static class AddNodeWorkerInfrastructureExtensions
{
    public static IHostApplicationBuilder AddNodeWorkerInfrastructure(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // workspace copy sensitive-file exclusion policy for the workspace copy (stateless, name-based).
        builder.Services.AddSingleton<ISensitiveFileExclusionService, SensitiveFileExclusionService>();
        builder.Services.AddSingleton<DeadLetterFlushService>();
        builder.Services.AddSingleton<IWorkerShutdownDrainService, WorkerShutdownDrainService>();
        builder.Services.AddSingleton<IOllamaModelService, OllamaModelService>();
        // Model-type classification service: resolves each model's effective kind (override ?? detected) over the
        // classification store, lazily probing /api/show and caching by digest. Scoped because it depends on the
        // scoped, DbContext-backed IModelClassificationStore (a singleton could not consume it); the singleton
        // IOllamaModelService is safe to consume from a scoped service.
        builder.Services.AddScoped<IModelClassificationService, ModelClassificationService>();

        return builder;
    }
}
