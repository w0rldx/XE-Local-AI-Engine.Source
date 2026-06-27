namespace XE_Local_AI_Engine.Providers.HuggingFace;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

/// <summary>
///     DI wiring for the public <see cref="IGgufMetadataReader" /> seam. Separate from
///     <see cref="HuggingFaceServiceCollectionExtensions.AddHuggingFaceGgufStore" /> so the optimizer can opt into the
///     header-metadata seam without re-registering the whole store stack.
/// </summary>
/// <remarks>
///     <strong>Caller contract:</strong> the consuming application must have called
///     <see cref="HuggingFaceServiceCollectionExtensions.AddHuggingFaceGgufStore" /> first — it registers the internal
///     <c>GgufHeaderReader</c> this seam wraps.
/// </remarks>
public static class GgufMetadataReaderServiceCollectionExtensions
{
    /// <summary>Registers the public <see cref="IGgufMetadataReader" /> over the internal GGUF header reader.</summary>
    public static IServiceCollection AddGgufMetadataReader(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IGgufMetadataReader>(static sp => new GgufMetadataReader(sp.GetRequiredService<GgufHeaderReader>(),
            sp.GetRequiredService<ILogger<GgufMetadataReader>>()));

        return services;
    }
}
