namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Client.Services.Images.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;

/// <summary>
///     Wires the local image-generation lane (Lane C jobs/store/hub-publisher) on top of the Lane A model store + Lane B
///     sd-server runtime adapter. Must run AFTER <c>AddNodeModelRuntime</c> so the shared Hugging Face download client
///     (registered by <c>AddHuggingFaceGgufStore</c>) the image model store reuses is present.
/// </summary>
internal static class AddNodeImagesExtensions
{
    public static IHostApplicationBuilder AddNodeImages(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Lane A image-model file-set store + registry (reuses the Hugging Face download client) and the Lane B
        // sd-server runtime adapter (binary manager, backend selector, supervisor, job client, IImageRuntime facade).
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

        // The on-demand image-job coordinator. Singleton: the in-flight registry must outlive the request that started a
        // job (generation runs detached), and it composes the singleton runtime + blob store + IHubContext-safe publisher.
        builder.Services.AddSingleton<IImageJobCoordinator, ImageJobCoordinator>();

        return builder;
    }
}
