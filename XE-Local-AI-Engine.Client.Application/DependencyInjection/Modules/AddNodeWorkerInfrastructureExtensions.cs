namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;

internal static class AddNodeWorkerInfrastructureExtensions
{
    /// <summary>
    ///     Registers the worker-side infrastructure the model surfaces read: the exclusion policy, model classification,
    ///     the picker catalog and the details resolver.
    /// </summary>
    /// <remarks>
    ///     <c>IOllamaModelService</c> is registered by <c>AddOllamaRuntime</c>, never here: it takes an
    ///     <c>IOllamaApiClient</c> that exists only when the Ollama gate is on, so an unconditional registration here
    ///     leaves a gate-off node unbuildable under <c>ValidateOnBuild</c> and throwing at each consumer's first
    ///     resolve. Both gate branches register it, so the services below always find one, and a singleton
    ///     <c>IOllamaModelService</c> is safe to consume from the scoped services here.
    /// </remarks>
    public static IHostApplicationBuilder AddNodeWorkerInfrastructure(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // workspace copy sensitive-file exclusion policy for the workspace copy (stateless, name-based).
        builder.Services.AddSingleton<ISensitiveFileExclusionService, SensitiveFileExclusionService>();
        // Model-type classification resolves each model's effective kind (override, else detected) over the classification store,
        // lazily probing /api/show and caching by digest. Scoped: it depends on the scoped, DbContext-backed IModelClassificationStore.
        builder.Services.AddScoped<IModelClassificationService, ModelClassificationService>();
        // Model-picker catalog: fans out over Ollama, the installed GGUFs and the two cloud providers, degrading each
        // source independently. Scoped because it consumes the scoped IModelClassificationService.
        builder.Services.AddScoped<ILocalModelCatalogService, LocalModelCatalogService>();
        // Model-details provider routing for the details endpoint: which of the five providers owns a model id, and what "details"
        // means for it. Scoped for the same reason as the catalog above — it consumes the scoped cloud and external resolvers.
        builder.Services.AddScoped<ILocalModelDetailsResolver, LocalModelDetailsResolver>();

        return builder;
    }
}
