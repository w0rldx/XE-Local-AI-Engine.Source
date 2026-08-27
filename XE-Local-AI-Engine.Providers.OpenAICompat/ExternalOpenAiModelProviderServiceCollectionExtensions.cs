namespace XE_Local_AI_Engine.Providers.OpenAICompat;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.OpenAICompat.Implementation;

/// <summary>
///     Dependency-injection helpers for wiring the external OpenAI-compatible implementation of
///     <see cref="ILocalModelProvider" />.
/// </summary>
public static class ExternalOpenAiModelProviderServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the external provider as a SINGLE <see cref="ILocalModelProvider" /> multiplexer serving every
    ///     configured connection.
    /// </summary>
    /// <remarks>
    ///     The caller owns registering <see cref="IExternalProviderRegistry" />; this extension deliberately supplies no
    ///     fallback. An empty-registry stand-in would let the provider register on a node whose external store is
    ///     missing or unreadable and quietly report zero models — which is indistinguishable, from the operator's side,
    ///     from "my connections were silently dropped". Composition roots therefore call this only once a real registry
    ///     is in the container.
    /// </remarks>
    public static IServiceCollection AddExternalOpenAiModelProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = services.AddSingleton<ILocalModelProvider>(serviceProvider =>
            new ExternalOpenAiModelProvider(serviceProvider.GetRequiredService<IExternalProviderRegistry>()));
        return services;
    }
}
