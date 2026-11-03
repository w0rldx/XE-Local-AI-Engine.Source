namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Models.Implementation;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;

internal static class AddNodeWorkerInfrastructureExtensions
{
    /// <summary>
    ///     Registers the worker-side infrastructure the model surfaces read: the exclusion policy, model classification,
    ///     the picker catalog and the details resolver.
    /// </summary>
    public static IHostApplicationBuilder AddNodeWorkerInfrastructure(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // workspace copy sensitive-file exclusion policy for the workspace copy (stateless, name-based).
        builder.Services.AddSingleton<ISensitiveFileExclusionService, SensitiveFileExclusionService>();
        // Model-type classification sets and resets an operator's kind override over the classification store.
        // Scoped: it depends on the scoped, DbContext-backed IModelClassificationStore.
        builder.Services.AddScoped<IModelClassificationService, ModelClassificationService>();
        // Model-picker catalog: fans out over the installed GGUFs and the two cloud providers, degrading each
        // source independently. Scoped because it consumes the scoped IModelClassificationStore.
        builder.Services.AddScoped<ILocalModelCatalogService, LocalModelCatalogService>();
        // Model-details provider routing for the details endpoint: which of the five providers owns a model id, and what "details"
        // means for it. Scoped for the same reason as the catalog above — it consumes the scoped cloud and external resolvers.
        builder.Services.AddScoped<ILocalModelDetailsResolver, LocalModelDetailsResolver>();

        return builder;
    }
}
