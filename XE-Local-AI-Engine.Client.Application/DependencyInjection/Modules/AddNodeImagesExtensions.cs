namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Client.Services.Images.Catalog;
using XE_Local_AI_Engine.Client.Services.Images.Catalog.Implementation;
using XE_Local_AI_Engine.Client.Services.Images.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;

/// <summary>
///     Wires the local image-generation stack (jobs, store, hub publisher) on top of the image-model store and the
///     sd-server runtime adapter.
/// </summary>
/// <remarks>
///     Must run AFTER <c>AddNodeModelRuntime</c>, so the shared Hugging Face download client that
///     <c>AddHuggingFaceGgufStore</c> registers, and the image model store reuses, is already present.
/// </remarks>
internal static class AddNodeImagesExtensions
{
    public static IHostApplicationBuilder AddNodeImages(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Image-model file-set store + registry (reuses the Hugging Face download client) and the sd-server
        // runtime adapter (binary manager, backend selector, supervisor, job client, IImageRuntime facade).
        builder.Services.AddHuggingFaceImageModelStore(configuration);
        // Registered BEFORE the provider modules: both TryAdd a bare default, so this is the only place the
        // StableDiffusionRuntime config section (port range, TTL, cap, TextEncoderOnGpu, ...) reaches the supervisor.
        builder.Services.AddSingleton(BindStableDiffusionRuntimeOptions(configuration));
        builder.Services.AddStableDiffusionCppImageProvider();
        builder.Services.AddStableDiffusionCppImageRuntime();

        // The image runtime/source-build endpoints' door onto IStableDiffusionCppSourceBuildService, its prerequisite probe,
        // IStableDiffusionInstalledRuntimeStore, IImageRuntimeActivityGate and IImageServerSupervisor. Singleton: stateless over their TryAddSingletons.
        builder.Services.AddSingleton<ImageRuntimeOrchestrationService>();

        // Curated image-model catalog (embedded seed). Singleton: the document is immutable and loading it means
        // reading + validating an assembly resource, which should happen once rather than per catalog request.
        builder.Services.AddSingleton<IImageModelCatalog, ImageModelCatalog>();

        // Persistence boundary for the job registry. Scoped: it owns a NodeChatDbContext per operation (the prompt is
        // encrypted at rest by the node encryption interceptor on save).
        builder.Services.AddScoped<IImageJobStore, ImageJobStore>();

        // Persistence boundary for the generated-image metadata rows. Scoped: it owns a NodeChatDbContext per operation.
        builder.Services.AddScoped<IGeneratedImageRowStore, GeneratedImageRowStore>();

        // Encrypted-at-rest generated-image blob store. Singleton: it opens a scope per row operation and depends only
        // on singletons (data directory, sqlite key holder, time provider) — the same posture as the uploaded-file store.
        builder.Services.AddSingleton<IGeneratedImageStore, GeneratedImageStore>();

        // No-op default image-job event publisher; the Client host supersedes it with the hub-backed publisher.
        builder.Services.AddSingleton<IImageJobEventPublisher, NullImageJobEventPublisher>();

        // Weight-download coordinator. Singleton for the same reason as the job coordinator: the download outlives the
        // request that started it, and its status registry is what makes a failed download observable at all.
        builder.Services.AddSingleton<IImageModelDownloadCoordinator, ImageModelDownloadCoordinator>();

        // The on-demand image-job coordinator. Singleton: the in-flight registry must outlive the request that started a
        // job (generation runs detached), and it composes the singleton runtime + blob store + IHubContext-safe publisher.
        builder.Services.AddSingleton<IImageJobCoordinator, ImageJobCoordinator>();

        // Startup reconciliation: after a death with jobs still Queued/Generating the coordinator's in-memory registry is gone, so
        // those rows are marked Failed with a content-free reason and an event — never auto-retried — before Kestrel accepts requests.
        builder.Services.AddHostedService<ImageJobStartupReconciler>();

        return builder;
    }

    /// <summary>The sd-server runtime options from the <c>StableDiffusionRuntime</c> section over the class defaults.</summary>
    internal static StableDiffusionRuntimeOptions BindStableDiffusionRuntimeOptions(IConfiguration configuration)
    {
        var options = new StableDiffusionRuntimeOptions();
        configuration.GetSection(StableDiffusionRuntimeOptions.SectionName).Bind(options);
        return options;
    }
}
