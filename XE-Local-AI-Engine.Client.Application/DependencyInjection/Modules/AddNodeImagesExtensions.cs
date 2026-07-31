namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Client.Services.Images.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;

/// <summary>
///     Wires the local image-generation stack (jobs/store/hub-publisher) on top of the image-model store + the sd-server
///     runtime adapter. Must run AFTER <c>AddNodeModelRuntime</c> so the shared Hugging Face download client
///     (registered by <c>AddHuggingFaceGgufStore</c>) the image model store reuses is present.
/// </summary>
internal static class AddNodeImagesExtensions
{
    public static IHostApplicationBuilder AddNodeImages(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Image-model file-set store + registry (reuses the Hugging Face download client) and the sd-server
        // runtime adapter (binary manager, backend selector, supervisor, job client, IImageRuntime facade).
        builder.Services.AddHuggingFaceImageModelStore(configuration);
        builder.Services.AddStableDiffusionCppImageProvider();
        builder.Services.AddStableDiffusionCppImageRuntime();

        // Persistence boundary for the job registry. Scoped: it owns a NodeChatDbContext per operation (the prompt is
        // encrypted at rest by the node encryption interceptor on save).
        builder.Services.AddScoped<IImageJobStore, ImageJobStore>();

        // Encrypted-at-rest generated-image blob store. Singleton: it opens its own DbContext scope per operation and
        // depends only on singletons (data directory, sqlite key holder, time provider) — same posture as the uploaded
        // file store.
        builder.Services.AddSingleton<IGeneratedImageStore, GeneratedImageStore>();

        // No-op default image-job event publisher; the Client host supersedes it with the hub-backed publisher.
        builder.Services.AddSingleton<IImageJobEventPublisher, NullImageJobEventPublisher>();

        // Weight-download coordinator. Singleton for the same reason as the job coordinator: the download outlives the
        // request that started it, and its status registry is what makes a failed download observable at all.
        builder.Services.AddSingleton<IImageModelDownloadCoordinator, ImageModelDownloadCoordinator>();

        // The on-demand image-job coordinator. Singleton: the in-flight registry must outlive the request that started a
        // job (generation runs detached), and it composes the singleton runtime + blob store + IHubContext-safe publisher.
        builder.Services.AddSingleton<IImageJobCoordinator, ImageJobCoordinator>();

        // Startup reconciliation: a previous process may have died with jobs still Queued/Generating; the coordinator's
        // in-memory registry is gone after a restart, so those rows would otherwise stay stuck forever. Interrupted jobs
        // are NOT auto-retried (image generation is expensive/nondeterministic) — they are marked Failed with a
        // content-free reason and a status event is pushed. Runs before Kestrel accepts requests (hosted services start
        // before the web host), so it cannot race a newly enqueued job.
        builder.Services.AddHostedService<ImageJobStartupReconciler>();

        return builder;
    }
}
