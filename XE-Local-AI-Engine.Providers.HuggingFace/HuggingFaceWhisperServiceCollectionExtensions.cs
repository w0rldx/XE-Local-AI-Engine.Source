namespace XE_Local_AI_Engine.Providers.HuggingFace;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

/// <summary>
///     DI wiring for the Whisper weight store, registered over the SAME <see cref="HfDownloadClient" /> the GGUF,
///     image and base-checkpoint lanes use.
/// </summary>
/// <remarks>
///     <strong>Caller contract:</strong> call
///     <see cref="HuggingFaceServiceCollectionExtensions.AddHuggingFaceGgufStore" /> first — it registers the shared
///     download client this store depends on.
/// </remarks>
public static class HuggingFaceWhisperServiceCollectionExtensions
{
    /// <summary>Registers the Whisper weight-file store.</summary>
    public static IServiceCollection AddHuggingFaceWhisperWeightStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IWhisperWeightFileStore>(static sp =>
            new HuggingFaceWhisperWeightStore(sp.GetRequiredService<HfDownloadClient>()));

        return services;
    }
}
