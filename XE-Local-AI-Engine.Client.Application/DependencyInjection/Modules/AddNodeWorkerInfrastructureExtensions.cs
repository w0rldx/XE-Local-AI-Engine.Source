namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.DeadLetter.Implementation;
using XE_Local_AI_Engine.Client.Services.Models;
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
        // Model-picker catalog: fans out over Ollama, the installed GGUFs and the two cloud providers, degrading each
        // source independently. Scoped because it consumes the scoped IModelClassificationService.
        builder.Services.AddScoped<ILocalModelCatalogService, LocalModelCatalogService>();

        return builder;
    }
}
