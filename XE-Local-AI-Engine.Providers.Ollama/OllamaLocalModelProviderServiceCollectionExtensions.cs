namespace XE_Local_AI_Engine.Providers.Ollama;

using Microsoft.Extensions.DependencyInjection;
using OllamaSharp;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Dependency-injection helpers for wiring the Ollama implementation of <see cref="ILocalModelProvider" />.
/// </summary>
public static class OllamaLocalModelProviderServiceCollectionExtensions
{
    /// <summary>
    ///     Registers a singleton Ollama API client plus the provider-neutral local-model and capability abstractions.
    /// </summary>
    /// <remarks>
    ///     The created <see cref="HttpClient" /> is intentionally owned by the singleton Ollama client. The five-minute
    ///     timeout covers model-management calls such as pulls and show/probe requests without forcing every caller to
    ///     allocate its own transport.
    /// </remarks>
    public static IServiceCollection AddOllamaLocalModelProvider(this IServiceCollection services,
        Func<IServiceProvider, OllamaLocalModelProviderRegistration> resolveRegistration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(resolveRegistration);

        _ = services.AddSingleton<IOllamaApiClient>(serviceProvider =>
        {
            var registration = resolveRegistration(serviceProvider);

#pragma warning disable CA2000 // HttpClient lifetime is owned by the registered Ollama client.
            var httpClient = new HttpClient
            {
                BaseAddress = registration.Endpoint,
                Timeout = TimeSpan.FromMinutes(5)
            };

            return new OllamaApiClient(httpClient)
            {
                SelectedModel = registration.Model
            };
#pragma warning restore CA2000
        });

        _ = services.AddSingleton<ILocalModelProvider, OllamaLocalModelProvider>();
        _ = services.AddSingleton<IModelCapabilityClient, OllamaModelCapabilityClient>();
        return services;
    }
}
