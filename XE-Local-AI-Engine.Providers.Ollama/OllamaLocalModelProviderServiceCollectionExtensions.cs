namespace XE_Local_AI_Engine.Providers.Ollama;

using Microsoft.Extensions.DependencyInjection;
using OllamaSharp;
using XE_Local_AI_Engine.Providers.Abstractions;

public static class OllamaLocalModelProviderServiceCollectionExtensions
{
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
        return services;
    }
}
