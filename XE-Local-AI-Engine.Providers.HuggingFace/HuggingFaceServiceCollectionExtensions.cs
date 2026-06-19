namespace XE_Local_AI_Engine.Providers.HuggingFace;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     DI wiring for the Hugging Face GGUF discovery + store stack. Registers <see cref="IGgufModelStore" />,
///     <see cref="IGgufModelRegistry" />, and <see cref="IHuggingFaceGgufDiscovery" /> plus the internal Hub/download
///     clients, header reader, free-space probe, named <see cref="HttpClient" />s, and bound options.
/// </summary>
/// <remarks>
///     <strong>Caller contract:</strong> the consuming application must register an <see cref="IHfTokenStore" /> (the
///     encrypted token store lives in the Application layer next to the other credential stores) before resolving the
///     store, since the download client depends on it.
/// </remarks>
public static class HuggingFaceServiceCollectionExtensions
{
    /// <summary>Named <see cref="HttpClient" /> for the Hub REST API (list/tree).</summary>
    public const string HubHttpClientName = "hf-hub";

    /// <summary>Named <see cref="HttpClient" /> for file downloads + GGUF header range reads.</summary>
    public const string DownloadHttpClientName = "hf-download";

    /// <summary>Registers the Hugging Face GGUF store, registry, and discovery over the documented Hub REST endpoints.</summary>
    public static IServiceCollection AddHuggingFaceGgufStore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new HuggingFaceOptions();
        configuration.GetSection(HuggingFaceOptions.SectionName).Bind(options);
        if (string.IsNullOrWhiteSpace(options.ModelsDirectory))
        {
            options.ModelsDirectory = Path.Combine(AppContext.BaseDirectory, "models");
        }

        services.TryAddSingleton(options);
        services.TryAddSingleton<IFreeSpaceProbe, DriveInfoFreeSpaceProbe>();

        services.AddHttpClient(HubHttpClientName);
        services.AddHttpClient(DownloadHttpClientName);

        services.TryAddSingleton(static sp => new HfHubClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(HubHttpClientName),
            sp.GetRequiredService<HuggingFaceOptions>(),
            sp.GetRequiredService<ILogger<HfHubClient>>()));

        services.TryAddSingleton(static sp => new GgufHeaderReader(sp.GetRequiredService<IHttpClientFactory>().CreateClient(DownloadHttpClientName),
            sp.GetRequiredService<HuggingFaceOptions>(),
            sp.GetRequiredService<ILogger<GgufHeaderReader>>()));

        services.TryAddSingleton<IHuggingFaceGgufDiscovery>(static sp => new HuggingFaceGgufDiscovery(sp.GetRequiredService<HfHubClient>(),
            sp.GetRequiredService<GgufHeaderReader>(),
            sp.GetRequiredService<HuggingFaceOptions>(),
            sp.GetRequiredService<ILogger<HuggingFaceGgufDiscovery>>()));

        services.TryAddSingleton(static sp => new HfDownloadClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(DownloadHttpClientName),
            sp.GetRequiredService<IHfTokenStore>(),
            sp.GetRequiredService<IFreeSpaceProbe>(),
            sp.GetRequiredService<HuggingFaceOptions>(),
            sp.GetRequiredService<ILogger<HfDownloadClient>>()));

        services.TryAddSingleton(static sp => new GgufModelRegistry(sp.GetRequiredService<HuggingFaceOptions>(),
            sp.GetRequiredService<ILogger<GgufModelRegistry>>()));
        services.TryAddSingleton<IGgufModelRegistry>(static sp => sp.GetRequiredService<GgufModelRegistry>());

        services.TryAddSingleton<IGgufModelStore>(static sp => new HuggingFaceGgufStore(sp.GetRequiredService<HfDownloadClient>(),
            sp.GetRequiredService<IHuggingFaceGgufDiscovery>(),
            sp.GetRequiredService<GgufModelRegistry>(),
            sp.GetRequiredService<HuggingFaceOptions>(),
            sp.GetRequiredService<ILogger<HuggingFaceGgufStore>>()));

        return services;
    }
}
