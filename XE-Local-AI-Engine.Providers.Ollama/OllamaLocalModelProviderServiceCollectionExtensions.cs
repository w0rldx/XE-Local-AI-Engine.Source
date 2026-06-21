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
    ///     The maximum time to wait for the TCP connection to the Ollama endpoint to establish before failing fast.
    ///     This bounds ONLY the connect phase (<see cref="SocketsHttpHandler.ConnectTimeout" />), not the overall
    ///     request: a long model pull still runs to the five-minute <see cref="HttpClient.Timeout" />. The short value
    ///     is the lever that kills the multi-second stall when no Ollama daemon is listening (desktop mode): a refused
    ///     or absent endpoint fails in well under a second instead of waiting out the OS connect timeout. A fired
    ///     connect timeout surfaces as an <see cref="OperationCanceledException" />, which
    ///     <see cref="OllamaConnectFailureHandler" /> translates to <see cref="HttpRequestException" /> so the
    ///     "Ollama unreachable" handling is uniform whether the host refuses (RST) or silently drops the SYN. It does not
    ///     shorten any genuine Ollama call once connected.
    /// </summary>
    private static readonly TimeSpan OllamaConnectTimeout = TimeSpan.FromMilliseconds(750);

    /// <summary>
    ///     Registers a singleton Ollama API client plus the provider-neutral local-model and capability abstractions.
    /// </summary>
    /// <remarks>
    ///     The created <see cref="HttpClient" /> is intentionally owned by the singleton Ollama client. The five-minute
    ///     timeout covers model-management calls such as pulls and show/probe requests without forcing every caller to
    ///     allocate its own transport. A short <see cref="SocketsHttpHandler.ConnectTimeout" /> bounds the connect phase
    ///     so a probe against an absent Ollama daemon fails fast (see <see cref="OllamaConnectTimeout" />).
    /// </remarks>
    public static IServiceCollection AddOllamaLocalModelProvider(this IServiceCollection services,
        Func<IServiceProvider, OllamaLocalModelProviderRegistration> resolveRegistration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(resolveRegistration);

        _ = services.AddSingleton<IOllamaApiClient>(serviceProvider =>
        {
            var registration = resolveRegistration(serviceProvider);

#pragma warning disable CA2000 // The handler and HttpClient lifetimes are owned by the registered singleton Ollama client.
            // SocketsHttpHandler.ConnectTimeout bounds only TCP connection establishment, so a refused/absent endpoint
            // fails fast while the 5-minute HttpClient.Timeout still covers genuine long pulls once connected. The
            // handler is owned by the HttpClient (disposeHandler: true), which the singleton OllamaApiClient owns.
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = OllamaConnectTimeout
            };

            // Wrap the connect-timeout handler so a fired ConnectTimeout (an OperationCanceledException) presents as an
            // HttpRequestException — the shape every "Ollama unreachable" catch in the codebase expects. The outer
            // handler is disposed by the HttpClient (disposeHandler: true) and disposes the inner one in turn.
            var connectFailureHandler = new OllamaConnectFailureHandler(handler);

            var httpClient = new HttpClient(connectFailureHandler, true)
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
