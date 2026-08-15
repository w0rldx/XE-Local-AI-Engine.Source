namespace XE_Local_AI_Engine.Providers.HuggingFace;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

/// <summary>
///     DI wiring for the trainable base-checkpoint store, registered over the SAME <see cref="HfDownloadClient" /> /
///     <see cref="HfHubClient" /> pair the GGUF and image lanes use.
/// </summary>
/// <remarks>
///     <strong>Caller contract:</strong> call
///     <see cref="HuggingFaceServiceCollectionExtensions.AddHuggingFaceGgufStore" /> first — it registers the shared
///     download and hub clients this store depends on.
/// </remarks>
public static class HuggingFaceBaseCheckpointServiceCollectionExtensions
{
    public static IServiceCollection AddHuggingFaceBaseCheckpointStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IBaseCheckpointStore>(static sp =>
            new HuggingFaceBaseCheckpointStore(sp.GetRequiredService<HfHubClient>(),
                sp.GetRequiredService<HfDownloadClient>(),
                sp.GetRequiredService<ILogger<HuggingFaceBaseCheckpointStore>>()));

        return services;
    }
}
