namespace XE_Local_AI_Engine.Providers.HuggingFace;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

/// <summary>
///     DI wiring for the Hugging Face image-model file-set store + registry + discovery. Registers
///     <see cref="IImageModelStore" />, <see cref="IImageModelRegistry" /> and <see cref="IImageModelDiscovery" /> over
///     the SAME reused <see cref="HfDownloadClient" /> / <see cref="HfHubClient" /> the GGUF lane uses (neither is ever
///     forked), plus the bound options.
/// </summary>
/// <remarks>
///     <strong>Caller contract:</strong> call <see cref="HuggingFaceServiceCollectionExtensions.AddHuggingFaceGgufStore" />
///     first — it registers the shared <see cref="HfDownloadClient" /> and <see cref="HfHubClient" /> (and their
///     <c>IHfTokenStore</c> / free-space probe / named <see cref="System.Net.Http.HttpClient" /> dependencies) that the
///     image store and discovery depend on.
/// </remarks>
public static class HuggingFaceImageServiceCollectionExtensions
{
    /// <summary>Registers the Hugging Face image-model store + registry over the reused Hugging Face download client.</summary>
    public static IServiceCollection AddHuggingFaceImageModelStore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new ImageModelStoreOptions();
        configuration.GetSection(ImageModelStoreOptions.SectionName).Bind(options);
        if (string.IsNullOrWhiteSpace(options.ModelsDirectory))
        {
            options.ModelsDirectory = Path.Combine(AppContext.BaseDirectory, "models", "images");
        }

        services.TryAddSingleton(options);

        services.TryAddSingleton(static sp => new ImageModelRegistry(sp.GetRequiredService<ImageModelStoreOptions>(),
            sp.GetRequiredService<ILogger<ImageModelRegistry>>()));
        services.TryAddSingleton<IImageModelRegistry>(static sp => sp.GetRequiredService<ImageModelRegistry>());

        services.TryAddSingleton<IImageModelStore>(static sp => new HuggingFaceImageModelStore(sp.GetRequiredService<HfDownloadClient>(),
            sp.GetRequiredService<ImageModelRegistry>(),
            sp.GetRequiredService<ImageModelStoreOptions>(),
            sp.GetRequiredService<ILogger<HuggingFaceImageModelStore>>()));

        // Image-model discovery rides the SAME HfHubClient the GGUF lane registers (shared listing cache, one HttpClient).
        services.TryAddSingleton<IImageModelDiscovery>(static sp => new HuggingFaceImageModelDiscovery(sp.GetRequiredService<HfHubClient>(),
            sp.GetRequiredService<ILogger<HuggingFaceImageModelDiscovery>>()));

        return services;
    }
}
